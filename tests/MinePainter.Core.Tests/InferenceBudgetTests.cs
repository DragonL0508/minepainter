using MinePainter.Core.AI;
using Xunit;

namespace MinePainter.Core.Tests;

/// <summary>
/// 去背開算前的記憶體預檢。實測背景：birefnet_lite 在 1024 解析度下 DirectML 要約 16 GB
/// （8 GB 的筆電獨顯裝不下，會溢流到系統記憶體把機器拖死），同一個模型走 CPU 只要 6.3 GB；
/// 而 isnet-general-use 檔案差不多大卻只要 0.5 GB。所以規則必須建立在實測值上，不能猜。
/// </summary>
public class InferenceBudgetTests : IDisposable
{
    private const long Gb = 1L << 30;
    private const int Size = 1024;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mp-budget-" + Guid.NewGuid().ToString("N"));
    private readonly string _storePath;
    private readonly string _previousStorePath = ModelCostStore.FilePath;
    private readonly Func<InferenceBudget.MachineMemory> _previousProbe = InferenceBudget.ProbeMachine;

    public InferenceBudgetTests()
    {
        Directory.CreateDirectory(_dir);
        _storePath = Path.Combine(_dir, "model-cost.json");
        ModelCostStore.FilePath = _storePath;
        ModelCostStore.Clear();
    }

    public void Dispose()
    {
        InferenceBudget.ProbeMachine = _previousProbe;
        ModelCostStore.FilePath = _previousStorePath;
        ModelCostStore.Clear();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private OnnxModelInfo Model(string name, long bytes = 1024)
    {
        var path = Path.Combine(_dir, name + ".onnx");
        File.WriteAllBytes(path, new byte[bytes]);
        return new OnnxModelInfo(name, path);
    }

    private static void Machine(long availableRam, long availableVram = 0, bool hasGpu = false) =>
        InferenceBudget.ProbeMachine = () => new InferenceBudget.MachineMemory(availableRam, availableVram, hasGpu);

    [Fact]
    public void UnmeasuredModelRunsOnCpuEvenWhenGpuRequested()
    {
        Machine(availableRam: 16 * Gb, availableVram: 8 * Gb, hasGpu: true);
        var plan = InferenceBudget.Plan(Model("brand-new"), Size, wantGpu: true);

        // 第一次跑的模型不知道要多少記憶體，先走可預測、可中止的 CPU
        Assert.Equal(InferenceProvider.Cpu, plan.Provider);
        Assert.Contains("第一次", plan.Note);
    }

    [Fact]
    public void LightModelGoesToGpuOnceItsCpuCostIsKnown()
    {
        var model = Model("isnet-general-use");
        ModelCostStore.Record(model, InferenceProvider.Cpu, Size, (long)(0.5 * Gb), failed: false);
        Machine(availableRam: 16 * Gb, availableVram: 8 * Gb, hasGpu: true);

        Assert.Equal(InferenceProvider.DirectMl, InferenceBudget.Plan(model, Size, wantGpu: true).Provider);
    }

    [Fact]
    public void HeavyModelStaysOnCpuBecauseItCannotFitInVideoMemory()
    {
        // birefnet_lite 的實測 CPU 峰值
        var model = Model("birefnet_lite");
        ModelCostStore.Record(model, InferenceProvider.Cpu, Size, (long)(6.3 * Gb), failed: false);
        Machine(availableRam: 16 * Gb, availableVram: 7 * Gb, hasGpu: true);

        var plan = InferenceBudget.Plan(model, Size, wantGpu: true);
        Assert.Equal(InferenceProvider.Cpu, plan.Provider);
        Assert.Contains("超過顯示卡", plan.Note);
    }

    [Fact]
    public void GpuIsNotUsedAgainAfterItRanOutOfMemory()
    {
        var model = Model("birefnet_lite");
        ModelCostStore.Record(model, InferenceProvider.DirectMl, Size, 8 * Gb, failed: true);
        ModelCostStore.Record(model, InferenceProvider.Cpu, Size, (long)(6.3 * Gb), failed: false);
        Machine(availableRam: 16 * Gb, availableVram: 8 * Gb, hasGpu: true);

        var plan = InferenceBudget.Plan(model, Size, wantGpu: true);
        Assert.Equal(InferenceProvider.Cpu, plan.Provider);
        Assert.Contains("中止", plan.Note);
    }

    [Fact]
    public void RefusesToStartWhenTheMachineCannotHoldTheMeasuredCost()
    {
        var model = Model("birefnet_lite");
        ModelCostStore.Record(model, InferenceProvider.Cpu, Size, (long)(6.3 * Gb), failed: false);
        Machine(availableRam: 3 * Gb);

        var e = Assert.Throws<InsufficientMemoryException>(() => InferenceBudget.Plan(model, Size, wantGpu: false));
        Assert.Contains("記憶體不足", e.Message);
        Assert.Contains("6.3 GB", e.Message);
    }

    [Fact]
    public void RefusesToProbeAnUnknownModelWhenMemoryIsAlreadyTight()
    {
        Machine(availableRam: 1 * Gb);
        Assert.Throws<InsufficientMemoryException>(() => InferenceBudget.Plan(Model("brand-new"), Size, wantGpu: false));
    }

    [Fact]
    public void PreviouslyAbortedRunNeedsTwiceTheRoomBeforeItIsRetried()
    {
        var model = Model("birefnet_lite");
        ModelCostStore.Record(model, InferenceProvider.Cpu, Size, 4 * Gb, failed: true);

        // 剛好比中止時的峰值多一點還不夠：那只是「至少要這麼多」
        Machine(availableRam: 6 * Gb);
        Assert.Throws<InsufficientMemoryException>(() => InferenceBudget.Plan(model, Size, wantGpu: false));

        // 記憶體真的變寬裕了才讓它再試一次
        Machine(availableRam: 12 * Gb);
        Assert.Equal(InferenceProvider.Cpu, InferenceBudget.Plan(model, Size, wantGpu: false).Provider);
    }

    [Fact]
    public void BudgetLeavesTheSafetyFloorForTheOperatingSystem()
    {
        Machine(availableRam: 10 * Gb);
        var plan = InferenceBudget.Plan(Model("brand-new"), Size, wantGpu: false);
        Assert.Equal(10 * Gb - InferenceBudget.SafetyFloorBytes, plan.BudgetBytes);
    }

    [Fact]
    public void MeasuredCostSurvivesAReload()
    {
        var model = Model("isnet-general-use");
        ModelCostStore.Record(model, InferenceProvider.Cpu, Size, 3 * Gb, failed: false);

        // 換一條路徑再指回同一個檔案，模擬重開 App
        ModelCostStore.FilePath = _storePath;
        Assert.Equal(3 * Gb, ModelCostStore.Get(model, InferenceProvider.Cpu, Size)!.PeakBytes);
    }

    [Fact]
    public void PeakCostNeverShrinks()
    {
        var model = Model("isnet-general-use");
        ModelCostStore.Record(model, InferenceProvider.Cpu, Size, 3 * Gb, failed: false);
        ModelCostStore.Record(model, InferenceProvider.Cpu, Size, 1 * Gb, failed: false);

        // 不同圖片的用量會有差異，取歷來最大值才不會低估
        Assert.Equal(3 * Gb, ModelCostStore.Get(model, InferenceProvider.Cpu, Size)!.PeakBytes);
    }

    [Fact]
    public void EditingTheModelFileInvalidatesItsMeasurement()
    {
        var model = Model("isnet-general-use");
        ModelCostStore.Record(model, InferenceProvider.Cpu, Size, 3 * Gb, failed: false);

        File.WriteAllBytes(model.Path, new byte[2048]); // 換了內容 = 換了模型
        Assert.Null(ModelCostStore.Get(model, InferenceProvider.Cpu, Size));
    }
}

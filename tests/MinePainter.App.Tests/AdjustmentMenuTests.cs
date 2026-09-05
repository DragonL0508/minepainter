using MinePainter.App.Views;
using MinePainter.Core.Adjustments;
using Xunit;

namespace MinePainter.App.Tests;

/// <summary>調整選單的順序表要涵蓋所有登錄的調整；漏列就會在選單上看不到（2026-09-06 曝光度／色溫踩過）。</summary>
public class AdjustmentMenuTests
{
    [Fact]
    public void 每個登錄的調整都在選單順序表裡()
    {
        foreach (var entry in AdjustmentRegistry.All)
            Assert.Contains(entry.TypeId, MainWindow.AdjustmentMenuOrderForTests);
    }
}

using SemanticAgentBroker.Shared.Configuration;

namespace SemanticAgentBroker.UnitTests.Shared.Configuration;

[TestClass]
public class ContextCompactionOptionsTests
{
    [TestMethod]
    public void GetEffectiveThreshold_ScalesThresholdBasedOnContextWindow()
    {
        var options = new ContextCompactionOptions
        {
            CompactionThreshold = 0.80
        };

        // 256K+: Max(0.80, 0.90) = 0.90
        Assert.AreEqual(0.90, options.GetEffectiveThreshold(300000), 0.001);
        Assert.AreEqual(0.90, options.GetEffectiveThreshold(262144), 0.001);

        // 64K+: Max(0.80, 0.85) = 0.85
        Assert.AreEqual(0.85, options.GetEffectiveThreshold(65536), 0.001);
        Assert.AreEqual(0.85, options.GetEffectiveThreshold(100000), 0.001);

        // 32K+: Min(0.80, 0.65) = 0.65
        Assert.AreEqual(0.65, options.GetEffectiveThreshold(32768), 0.001);
        Assert.AreEqual(0.65, options.GetEffectiveThreshold(40000), 0.001);

        // 16K+: Min(0.80, 0.55) = 0.55
        Assert.AreEqual(0.55, options.GetEffectiveThreshold(16384), 0.001);
        Assert.AreEqual(0.55, options.GetEffectiveThreshold(20000), 0.001);

        // 8K+: Min(0.80, 0.45) = 0.45
        Assert.AreEqual(0.45, options.GetEffectiveThreshold(8192), 0.001);
        Assert.AreEqual(0.45, options.GetEffectiveThreshold(10000), 0.001);

        // <8K: 0.40
        Assert.AreEqual(0.40, options.GetEffectiveThreshold(4096), 0.001);
        Assert.AreEqual(0.40, options.GetEffectiveThreshold(2000), 0.001);
    }

    [TestMethod]
    public void GetEffectiveThreshold_WhenCompactionThresholdIsVeryLow_RespectsLowerUserThresholdForSmallContexts()
    {
        var options = new ContextCompactionOptions
        {
            CompactionThreshold = 0.30
        };

        // 32K+: Min(0.30, 0.65) = 0.30
        Assert.AreEqual(0.30, options.GetEffectiveThreshold(32768), 0.001);

        // 16K+: Min(0.30, 0.55) = 0.30
        Assert.AreEqual(0.30, options.GetEffectiveThreshold(16384), 0.001);

        // 8K+: Min(0.30, 0.45) = 0.30
        Assert.AreEqual(0.30, options.GetEffectiveThreshold(8192), 0.001);
    }

    [TestMethod]
    public void GetEffectiveKeepCount_ScalesKeepCountBasedOnContextWindow()
    {
        var options = new ContextCompactionOptions
        {
            KeepRecentMessages = 6
        };

        // >= 65536: Max(6, 10) = 10
        Assert.AreEqual(10, options.GetEffectiveKeepCount(65536));
        Assert.AreEqual(10, options.GetEffectiveKeepCount(128000));

        // >= 32768: Max(6, 8) = 8
        Assert.AreEqual(8, options.GetEffectiveKeepCount(32768));
        Assert.AreEqual(8, options.GetEffectiveKeepCount(50000));

        // >= 16384: Min(6, 4) = 4
        Assert.AreEqual(4, options.GetEffectiveKeepCount(16384));
        Assert.AreEqual(4, options.GetEffectiveKeepCount(20000));

        // >= 8192: Min(6, 2) = 2
        Assert.AreEqual(2, options.GetEffectiveKeepCount(8192));
        Assert.AreEqual(2, options.GetEffectiveKeepCount(10000));

        // < 8192: 2
        Assert.AreEqual(2, options.GetEffectiveKeepCount(4096));
    }

    [TestMethod]
    public void GetEffectiveKeepCount_WhenUserConfiguresHighKeepCount_RespectsForLargeContexts()
    {
        var options = new ContextCompactionOptions
        {
            KeepRecentMessages = 15
        };

        // >= 65536: Max(15, 10) = 15
        Assert.AreEqual(15, options.GetEffectiveKeepCount(65536));

        // >= 32768: Max(15, 8) = 15
        Assert.AreEqual(15, options.GetEffectiveKeepCount(32768));

        // But clamps for smaller contexts to prevent immediate overflow
        Assert.AreEqual(4, options.GetEffectiveKeepCount(16384));
        Assert.AreEqual(2, options.GetEffectiveKeepCount(8192));
    }

    [TestMethod]
    public void GetMaxMessagesBeforeForceCompaction_ReturnsExpectedCeilings()
    {
        var options = new ContextCompactionOptions();

        Assert.AreEqual(100, options.GetMaxMessagesBeforeForceCompaction(300000));
        Assert.AreEqual(100, options.GetMaxMessagesBeforeForceCompaction(262144));
        Assert.AreEqual(60, options.GetMaxMessagesBeforeForceCompaction(65536));
        Assert.AreEqual(40, options.GetMaxMessagesBeforeForceCompaction(32768));
        Assert.AreEqual(20, options.GetMaxMessagesBeforeForceCompaction(16384));
        Assert.AreEqual(12, options.GetMaxMessagesBeforeForceCompaction(8192));
        Assert.AreEqual(8, options.GetMaxMessagesBeforeForceCompaction(4096));
    }

    [TestMethod]
    public void ContextCompactionOptions_DefaultValues_AreProperlyInitialized()
    {
        var options = new ContextCompactionOptions();

        Assert.AreEqual(0.80, options.CompactionThreshold);
        Assert.AreEqual(6, options.KeepRecentMessages);
        Assert.AreEqual(3, options.RecentObservationWindowSize);
        Assert.AreEqual(1200, options.MaxSummaryTokens);
        Assert.IsTrue(options.EnableStructuredStateTracking);
        Assert.AreEqual(5, options.NudgeReductionAfterStep);
        Assert.AreEqual(1500, options.MaxStepLogChars);
        Assert.AreEqual(16000, options.MaxStateInjectionTokens);
    }

    [TestMethod]
    public void GetEffectiveThreshold_SmallContext_RespectsLowerUserThreshold()
    {
        var options = new ContextCompactionOptions
        {
            CompactionThreshold = 0.25
        };

        // < 8K: should be Min(0.25, 0.40) = 0.25, NOT hardcoded 0.40
        Assert.AreEqual(0.25, options.GetEffectiveThreshold(4096), 0.001);
        Assert.AreEqual(0.25, options.GetEffectiveThreshold(2048), 0.001);
    }

    [TestMethod]
    public void GetEffectiveKeepCount_SmallContext_RespectsLowerUserKeepCount()
    {
        var options = new ContextCompactionOptions
        {
            KeepRecentMessages = 1
        };

        // < 8K: should be Min(1, 2) = 1, NOT hardcoded 2
        Assert.AreEqual(1, options.GetEffectiveKeepCount(4096));
        Assert.AreEqual(1, options.GetEffectiveKeepCount(2048));
    }
}

using NUnit.Framework;
using VoxelEngine.Core.Rendering;

public class EdgeBlurSettingsTests
{
    [Test]
    public void Defaults_AreWithinExpectedRanges()
    {
        var settings = new VoxelRaytraceSettings();
        Assert.That(settings.edgeWidthPercent, Is.InRange(0.01f, 0.5f));
        Assert.That(settings.edgeRenderScale, Is.InRange(0.1f, 1.0f));
        Assert.That(settings.enableEdgeBlur, Is.True);
    }
}

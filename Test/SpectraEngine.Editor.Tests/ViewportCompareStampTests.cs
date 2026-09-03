using SpectraEngine.Core.Diagnostics;
using SpectraEngine.Core.Graphics;
using System;
using System.IO;

namespace SpectraEngine.Editor.Tests;

/// <summary>
/// The colour verdict <c>--viewport-compare</c> leaves behind for the shell.
/// </summary>
/// <remarks>
/// <b>It exists because a double sRGB encode is invisible to everything else.</b>
/// The comparison runs in the demo executable against a windowless composited
/// surface, and the thing that has to act on it is the editor shell, which is a
/// different process. What matters here is that a verdict can only be claimed for
/// the machine it was actually measured on, and that nothing about the file can
/// stop a shell from starting.
/// </remarks>
public sealed class ViewportCompareStampTests
{
    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), "spectra-tests", Path.GetRandomFileName(), "viewport-compare.json");

    [Fact]
    public void A_verdict_survives_a_round_trip()
    {
        string path = TempPath();
        var stamp = new ViewportCompareStamp(
            "Intel(R) UHD Graphics 770", GraphicsBackend.D3D12, Green: true,
            new DateTime(2026, 9, 3, 8, 0, 0, DateTimeKind.Utc));

        stamp.Save(path).ShouldBeTrue();

        ViewportCompareStamp? loaded = ViewportCompareStamp.Load(path);

        loaded.ShouldNotBeNull();
        loaded.Adapter.ShouldBe("Intel(R) UHD Graphics 770");
        loaded.Backend.ShouldBe(GraphicsBackend.D3D12);
        loaded.Green.ShouldBeTrue();
        loaded.RecordedUtc.ShouldBe(stamp.RecordedUtc);
    }

    [Fact]
    public void A_verdict_is_only_green_for_the_backend_it_was_measured_on()
    {
        var stamp = new ViewportCompareStamp("Adapter A", GraphicsBackend.D3D11, true, DateTime.UtcNow);

        ViewportCompareStamp.IsGreenFor(stamp, GraphicsBackend.D3D11).ShouldBeTrue();

        // D3D11 hands its resolve target over directly and D3D12 goes through a
        // D3D11On12 bridge, so a verdict for one says nothing about the other.
        ViewportCompareStamp.IsGreenFor(stamp, GraphicsBackend.D3D12).ShouldBeFalse();
    }

    [Fact]
    public void No_measurement_is_not_a_green_one()
    {
        ViewportCompareStamp.Load(TempPath()).ShouldBeNull();
        ViewportCompareStamp.IsGreenFor(null, GraphicsBackend.D3D11).ShouldBeFalse();
    }

    [Fact]
    public void A_red_verdict_is_recorded_and_read_back_as_red()
    {
        // Recorded either way. A stamp that only ever appeared on success would
        // let a machine keep the previous run's green answer after breaking.
        string path = TempPath();
        new ViewportCompareStamp("Adapter A", GraphicsBackend.D3D11, false, DateTime.UtcNow)
            .Save(path).ShouldBeTrue();

        ViewportCompareStamp? loaded = ViewportCompareStamp.Load(path);

        loaded.ShouldNotBeNull();
        ViewportCompareStamp.IsGreenFor(loaded, GraphicsBackend.D3D11).ShouldBeFalse();
    }

    [Fact]
    public void A_damaged_or_half_written_file_is_no_measurement_rather_than_an_error()
    {
        string path = TempPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        File.WriteAllText(path, "{ \"adapter\": \"Adapter A\", ");
        ViewportCompareStamp.Load(path).ShouldBeNull();

        // A stamp naming no backend is a verdict about nothing, so it is the
        // same as no stamp at all rather than one that matches whatever it is
        // asked about.
        File.WriteAllText(path, "{ \"green\": true }");
        ViewportCompareStamp.Load(path).ShouldBeNull();
    }
}

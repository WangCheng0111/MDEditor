using System.Diagnostics;
using System.Xml.Linq;

namespace MDEditor.Core.Tests;

[TestClass]
public sealed class ReleaseConfigurationTests
{
    private static readonly string Root = Path.Combine(AppContext.BaseDirectory, "Architecture");
    private static XDocument App() => XDocument.Load(Path.Combine(Root, "MDEditor", "MDEditor.csproj"));

    [TestMethod]
    public void Build_defaults_do_not_generate_an_installer_and_keep_the_shared_runtime()
    {
        var app = App();
        Assert.AreEqual("true", app.Descendants("SelfContained").Single().Value);
        Assert.AreEqual("False", app.Descendants("PublishSingleFile").Single().Value);
        var packaging = app.Descendants("GenerateAppxPackageOnBuild").Single();
        Assert.AreEqual("false", packaging.Value);
        Assert.IsTrue(packaging.Attribute("Condition")!.Value.Contains("== ''", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow("x64", "win-x64")]
    [DataRow("x86", "win-x86")]
    [DataRow("ARM64", "win-arm64")]
    public void Shared_publish_profile_has_a_matching_architecture_and_no_credentials(string platform, string rid)
    {
        var profile = XDocument.Load(Path.Combine(Root, "PublishProfiles", rid + ".pubxml"));
        string Value(string name) => profile.Descendants().Single(element => element.Name.LocalName == name).Value;
        Assert.AreEqual(platform, Value("Platform"));
        Assert.AreEqual(rid, Value("RuntimeIdentifier"));
        Assert.AreEqual("true", Value("SelfContained"));
        Assert.AreEqual("False", Value("PublishSingleFile"));
        Assert.IsFalse(profile.Descendants().Any(element =>
            element.Name.LocalName.Contains("Password", StringComparison.OrdinalIgnoreCase) ||
            element.Name.LocalName.Contains("Thumbprint", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void Build_rejects_mixed_architectures_and_incomplete_runtime_settings()
    {
        var target = App().Descendants("Target").Single(element =>
            (string?)element.Attribute("Name") == "ValidateReleaseConfiguration");
        Assert.AreEqual("PrepareForBuild", (string?)target.Attribute("BeforeTargets"));
        var errors = target.Elements("Error").Select(element => (string)element.Attribute("Condition")!).ToArray();
        foreach (var rid in new[] { "win-x64", "win-x86", "win-arm64" })
            Assert.IsTrue(errors.Any(error => error.Contains(rid, StringComparison.Ordinal)));
        foreach (var setting in new[] { "SelfContained", "PublishTrimmed", "PublishSingleFile" })
            Assert.IsTrue(errors.Any(error => error.Contains(setting, StringComparison.Ordinal)));
    }

    [TestMethod]
    public void Worker_assembly_is_published_and_never_reused_as_stale_ready_to_run()
    {
        var app = App();
        var worker = app.Descendants("Content").Single(element =>
            (string?)element.Attribute("Link") == "MDEditor.MathWorker.dll");
        Assert.AreEqual("PreserveNewest", (string?)worker.Attribute("CopyToPublishDirectory"));
        CollectionAssert.AreEquivalent(new[] { "MDEditor.dll", "MDEditor.Core.dll", "MDEditor.Native.dll",
            "MDEditor.Typesetting.dll", "MDEditor.MathWorker.dll" }, app.Descendants("PublishReadyToRunExclude")
                .Select(element => (string)element.Attribute("Include")!).ToArray());
    }

    [TestMethod]
    public void Package_does_not_add_a_network_capability_or_an_extra_user_entry()
    {
        var manifest = XDocument.Load(Path.Combine(Root, "Package.appxmanifest"));
        Assert.HasCount(1, manifest.Descendants().Where(element => element.Name.LocalName == "Application").ToArray());
        Assert.IsFalse(manifest.Descendants().Where(element => element.Name.LocalName == "Capability")
            .Any(element => ((string?)element.Attribute("Name")) is "internetClient" or "internetClientServer" or "privateNetworkClientServer"));
    }

    [TestMethod]
    public async Task Payload_verifier_accepts_complete_payloads_and_rejects_corruption()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("The PowerShell release audit is a Windows developer tool.");
        var start = new ProcessStartInfo("pwsh") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-File",
            Path.Combine(Root, "Release", "Verify-ReleasePayloadTests.ps1") }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        Assert.AreEqual(0, process.ExitCode, await stdout + "\n" + await stderr);
        StringAssert.Contains(await stdout, "Release payload fixtures passed: 12");
    }
}

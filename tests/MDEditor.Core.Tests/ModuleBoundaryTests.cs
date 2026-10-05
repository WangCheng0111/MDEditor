using System.Reflection;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MDEditor.Core.Tests;

[TestClass]
public sealed class ModuleBoundaryTests
{
    private static readonly string MetadataRoot = Path.Combine(AppContext.BaseDirectory, "Architecture");

    [TestMethod]
    [DataRow("MDEditor.Core", "Core")]
    [DataRow("MDEditor.Typesetting", "Typesetting")]
    public void Portable_module_loads_without_WinUI(string assemblyName, string moduleName)
    {
        var assembly = Assembly.Load(new AssemblyName(assemblyName));

        Assert.AreEqual(".NETCoreApp,Version=v10.0",
            assembly.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName);
        Assert.IsNull(assembly.GetCustomAttribute<SupportedOSPlatformAttribute>());
        Assert.IsTrue(assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Any(attribute => attribute.Key == "MDEditor.Module" && attribute.Value == moduleName));
        Assert.IsFalse(assembly.GetReferencedAssemblies()
            .Any(reference => IsPlatformDependency(reference.Name ?? "")));
    }

    [TestMethod]
    [DataRow("MDEditor.Core")]
    [DataRow("MDEditor.Typesetting")]
    public void Portable_project_has_no_platform_packages_or_flags(string moduleName)
    {
        var project = LoadProject($"{moduleName}/{moduleName}.csproj");

        Assert.AreEqual("net10.0", Property(project, "TargetFramework"));
        Assert.IsFalse(project.Descendants("PackageReference")
            .Any(reference => IsPlatformDependency((string?)reference.Attribute("Include") ?? "")));
        Assert.IsFalse(new[] { "UseWinUI", "UseWPF", "UseWindowsForms" }
            .Any(name => string.Equals(Property(project, name), "true", StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(string.IsNullOrEmpty(Property(project, "RuntimeIdentifiers")));
        Assert.IsTrue(string.IsNullOrEmpty(Property(project, "RuntimeIdentifier")));
    }

    [TestMethod]
    [DataRow("MDEditor.Core/MDEditor.Core.csproj", "")]
    [DataRow("MDEditor.Typesetting/MDEditor.Typesetting.csproj", "MDEditor.Core")]
    [DataRow("MDEditor.Native/MDEditor.Native.csproj", "MDEditor.Core;MDEditor.Typesetting")]
    [DataRow("MDEditor.MathWorker/MDEditor.MathWorker.csproj", "MDEditor.Typesetting")]
    [DataRow("MDEditor/MDEditor.csproj", "MDEditor.Core;MDEditor.Native;MDEditor.Typesetting")]
    [DataRow("tests/MDEditor.Core.Tests/MDEditor.Core.Tests.csproj", "MDEditor.Core;MDEditor.Typesetting")]
    public void Project_dependencies_follow_the_declared_direction(string projectPath, string expectedModules)
    {
        var references = LoadProject(projectPath).Descendants("ProjectReference")
            .Where(reference => !string.Equals((string?)reference.Attribute("ReferenceOutputAssembly"), "false",
                StringComparison.OrdinalIgnoreCase))
            .Select(reference => (string?)reference.Attribute("Include") ?? "")
            .Select(path => Path.GetFileNameWithoutExtension(path.Replace('\\', '/')))
            .ToArray();
        var expected = expectedModules.Split(';', StringSplitOptions.RemoveEmptyEntries);

        CollectionAssert.AreEquivalent(expected, references);
    }

    [TestMethod]
    public void Test_project_and_runtime_graph_do_not_depend_on_the_Windows_app()
    {
        var project = LoadProject("tests/MDEditor.Core.Tests/MDEditor.Core.Tests.csproj");
        Assert.AreEqual("net10.0", Property(project, "TargetFramework"));

        // This is the resolved transitive graph, not just the direct project references.
        using var dependencies = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "MDEditor.Core.Tests.deps.json")));
        var libraryNames = dependencies.RootElement.GetProperty("libraries").EnumerateObject()
            .Select(library => library.Name.Split('/')[0])
            .ToArray();

        CollectionAssert.Contains(libraryNames, "MDEditor.Core");
        CollectionAssert.Contains(libraryNames, "MDEditor.Typesetting");
        Assert.IsFalse(libraryNames.Any(name => name == "MDEditor" || IsPlatformDependency(name)));
    }

    [TestMethod]
    public void Native_adapter_keeps_the_Windows_boundary()
    {
        var project = LoadProject("MDEditor.Native/MDEditor.Native.csproj");

        Assert.AreEqual("net10.0-windows10.0.19041.0", Property(project, "TargetFramework"));
        Assert.AreEqual("10.0.17763.0", Property(project, "TargetPlatformMinVersion"));
    }

    [TestMethod]
    public void Math_worker_is_a_self_contained_process_without_WinUI_or_app_reference()
    {
        var project = LoadProject("MDEditor.MathWorker/MDEditor.MathWorker.csproj");
        Assert.AreEqual("Exe", Property(project, "OutputType"));
        Assert.AreEqual("net10.0-windows10.0.19041.0", Property(project, "TargetFramework"));
        Assert.AreEqual("true", Property(project, "SelfContained"));
        Assert.AreEqual("win-x86;win-x64;win-arm64", Property(project, "RuntimeIdentifiers"));
        Assert.IsFalse(project.Descendants("PackageReference").Any());
        CollectionAssert.AreEquivalent(new[] { "MDEditor.Typesetting" }, project.Descendants("ProjectReference")
            .Select(reference => Path.GetFileNameWithoutExtension((string?)reference.Attribute("Include") ?? "")).ToArray());
    }

    [TestMethod]
    public void Application_packages_worker_without_loading_its_assembly()
    {
        var project = LoadProject("MDEditor/MDEditor.csproj");
        var worker = project.Descendants("ProjectReference").Single(reference =>
            Path.GetFileNameWithoutExtension((string?)reference.Attribute("Include") ?? "") == "MDEditor.MathWorker");
        Assert.AreEqual("false", (string?)worker.Attribute("ReferenceOutputAssembly"));
        Assert.IsTrue(project.Descendants("Content").Any(content =>
            ((string?)content.Attribute("Link")) == "MDEditor.MathWorker.dll"));
    }

    [TestMethod]
    public void Application_package_never_trims_assemblies_needed_by_the_worker()
    {
        var project = LoadProject("MDEditor/MDEditor.csproj");
        var setting = project.Descendants("PublishTrimmed").Single();
        Assert.AreEqual("False", setting.Value);
        Assert.IsNull(setting.Attribute("Condition"));
    }

    [TestMethod]
    public void Solution_contains_all_modules_and_maps_platforms_correctly()
    {
        var solution = XDocument.Load(MetadataPath("MDEditor/MDEditor.slnx"));
        var projects = solution.Descendants("Project").ToArray();
        var expected = new[]
        {
            "MDEditor", "MDEditor.Core", "MDEditor.Typesetting",
            "MDEditor.Native", "MDEditor.MathWorker", "MDEditor.Core.Tests"
        };
        CollectionAssert.AreEquivalent(expected, projects
            .Select(project => Path.GetFileNameWithoutExtension((string?)project.Attribute("Path") ?? ""))
            .ToArray());

        foreach (var project in projects)
        {
            var name = Path.GetFileNameWithoutExtension((string?)project.Attribute("Path") ?? "");
            foreach (var platform in new[] { "ARM64", "x64", "x86" })
            {
                var mapping = project.Elements("Platform").Single(element =>
                    (string?)element.Attribute("Solution") == $"*|{platform}");
                Assert.AreEqual(name is "MDEditor" or "MDEditor.Native" or "MDEditor.MathWorker" ? platform : "AnyCPU",
                    (string?)mapping.Attribute("Project"));
            }
        }
    }

    [TestMethod]
    public void Application_keeps_its_existing_identity_and_platform_contract()
    {
        var project = LoadProject("MDEditor/MDEditor.csproj");

        Assert.AreEqual("MDEditor", Property(project, "RootNamespace"));
        Assert.AreEqual("WinExe", Property(project, "OutputType"));
        Assert.AreEqual("net10.0-windows10.0.19041.0", Property(project, "TargetFramework"));
        Assert.AreEqual("10.0.17763.0", Property(project, "TargetPlatformMinVersion"));
        Assert.AreEqual("true", Property(project, "UseWinUI"));
        Assert.AreEqual("true", Property(project, "EnableMsixTooling"));
        Assert.IsNull(project.Descendants("AssemblyName").SingleOrDefault());
    }

    [TestMethod]
    public void Dotnet_test_uses_the_Microsoft_Testing_Platform_runner()
    {
        using var configuration = JsonDocument.Parse(File.ReadAllText(MetadataPath("global.json")));

        Assert.AreEqual("Microsoft.Testing.Platform",
            configuration.RootElement.GetProperty("test").GetProperty("runner").GetString());
        Assert.IsFalse(configuration.RootElement.TryGetProperty("sdk", out _));
    }

    private static XDocument LoadProject(string relativePath) => XDocument.Load(MetadataPath(relativePath));

    private static string MetadataPath(string relativePath) =>
        Path.Combine(MetadataRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static string? Property(XDocument project, string name) =>
        project.Descendants(name).Select(element => element.Value).SingleOrDefault();

    private static bool IsPlatformDependency(string name) =>
        name.Equals("MDEditor.Native", StringComparison.OrdinalIgnoreCase) ||
        new[]
        {
            "Microsoft.UI", "Microsoft.WindowsAppSDK", "Microsoft.Windows.SDK",
            "Microsoft.Graphics.Canvas", "Microsoft.Graphics.Win2D",
            "Microsoft.WindowsDesktop", "WinRT", "WindowsBase", "PresentationFramework",
            "PresentationCore", "System.Windows.Forms"
        }.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
}

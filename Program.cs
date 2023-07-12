using System.Data.Common;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using static System.Console;

try
{
    var sourceDirectory = args.Length == 0
        ? Directory.GetCurrentDirectory()
        : args[0];
    var tfm = GetTargetFramework();
    var info = new ProjectInfo()
    {
        TargetFramework = GetTargetFramework()
    };

    info.SourceFiles.AddRange(
        Directory
            .EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories));

    if (info.SourceFiles.Count == 0)
    {
        WriteLine("No source files found");
        return 1;
    }

    var destDirectory = Path.Combine(Path.GetTempPath(), "runf", Guid.NewGuid().ToString());
    Directory.CreateDirectory(destDirectory);

    CopySourceFiles(info, sourceDirectory, destDirectory);
    CreateProjectFile(info, destDirectory);
    return RunProjectFile(destDirectory);
}
catch (Exception ex)
{
    WriteLine(ex.Message);
    return 1;
}

string GetTargetFramework()
{
    var regex = new Regex(@"\.NET (\d+)");
    if (regex.Match(RuntimeInformation.FrameworkDescription) is { Success: true } match)
    {
        var version = int.Parse(match.Groups[1].Value);
        return $"net{version}.0";
    }

    throw new Exception($"Cannot determine runtime version: {RuntimeInformation.FrameworkDescription}");
}

void CopySourceFiles(ProjectInfo projectInfo, string sourceDirectory, string destDirectory)
{
    var packageMap = new Dictionary<string, string?>();
    foreach (var sourceFilePath in projectInfo.SourceFiles)
    {
        var destFilePath = sourceFilePath.Substring(sourceDirectory.Length);
        if (destFilePath.StartsWith(Path.DirectorySeparatorChar))
        {
            destFilePath = destFilePath.Substring(1);
        }
        destFilePath = Path.Combine(destDirectory, destFilePath);
        var content = File.ReadAllText(sourceFilePath);
        if (Path.GetExtension(sourceFilePath) == ".cs")
        {
            content = ScanForDirectives(content);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(destFilePath)!);
        File.WriteAllText(destFilePath, content);
    }

    projectInfo.Packages.AddRange(packageMap
        .OrderBy(x => x.Key)
        .Select(x => new Package(x.Key, x.Value)));

    string ScanForDirectives(string content)
    {
        if (content.IndexOf("#r") < 0)
        {
            return content;
        }

        var newContent = new StringBuilder();
        var reader = new StringReader(content);
        var any = false;
        while (reader.ReadLine() is { } line)
        {
            if (line.StartsWith("#r"))
            {
                var items = line.Substring(2).Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var name = items[0];
                var version = items.Length > 1 ? items[1] : null;
                packageMap[name] = version;
                any = true;
            }
            else
            {
                newContent.AppendLine(line);
            }
        }

        return any
            ? newContent.ToString()
            : content;
    }
}

void CreateProjectFile(ProjectInfo projectInfo, string destDirectory)
{
    string itemGroups = "";
    if (projectInfo.Packages.Count > 0)
    {
        var builder = new StringBuilder();
        foreach (var package in projectInfo.Packages)
        {
            var version = package.Version is not null
                ? $@"Version=""{package.Version}"""
                : "";
            builder.AppendLine($@"    <PackageReference Include=""{package.Name}"" {version} />");
        }

        itemGroups = builder.ToString();
    }

    var content = $"""
        <Project Sdk="Microsoft.NET.Sdk">

        <PropertyGroup>
            <OutputType>Exe</OutputType>
            <TargetFramework>{projectInfo.TargetFramework}</TargetFramework>
            <ImplicitUsings>enable</ImplicitUsings>
        </PropertyGroup>

        <ItemGroup>
        {itemGroups}
        </ItemGroup>

        </Project>
    """;

    File.WriteAllText(Path.Combine(destDirectory, "app.csproj"), content);
}

int RunProjectFile(string destDirectory)
{
    var psi = new ProcessStartInfo()
    {
        FileName = FindDotnetPath(),
        Arguments = "run",
        WorkingDirectory = destDirectory,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
    };
    var process = new Process()
    {
        StartInfo = psi,
    };

    process.Start();
    WriteLine(process.StandardOutput.ReadToEnd());
    WriteLine(process.StandardError.ReadToEnd());
    process.WaitForExit();
    return process.ExitCode;
}

string FindDotnetPath()
{
    var (fileName, sep) = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
        ? ("dotnet.exe", ';')
        : ("dotnet", ':');

    var path = Environment.GetEnvironmentVariable("PATH") ?? "";
    foreach (var item in path.Split(sep, StringSplitOptions.RemoveEmptyEntries))
    {
        try
        {
            var filePath = Path.Combine(item, fileName);
            if (File.Exists(filePath))
            {
                return filePath;
            }
        }
        catch
        {
            // If we can't read a directory for any reason just skip it
        }
    }

    return fileName;
}

internal class Package
{
    internal string Name { get;}
    internal string? Version { get; }

    internal Package(string name, string? version)
    {
        Name = name;
        Version = version;
    }
}

internal class ProjectInfo
{
    internal string? TargetFramework { get; set;}
    internal List<string> SourceFiles { get; set; } = new();
    internal List<Package> Packages { get; set;}  = new();
}
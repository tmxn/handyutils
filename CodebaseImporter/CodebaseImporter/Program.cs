using System;
using System.IO;
using System.Linq;

class CodebaseImporter
{
    // CONFIGURATION
    static string SourceRepo = @"S:\FastGitRoot\appmain";
    static string StagingFolder = @"E:\test\staginganythingllm";

    // Extensions to include
    static string[] AllowedExtensions = { ".cpp", ".h", ".hpp", ".cs", ".xaml", ".sln", ".csproj", ".vcxproj" };

    // Directories to ignore
    static string[] ExcludedDirs = { ".git", ".vs", "bin", "obj", "packages", "external", "node_modules", "Release", "Debug", "lemon", "include" };

    static void Main()
    {
        Console.WriteLine("🚀 Starting Codebase Clean-and-Copy...");
        if (Directory.Exists(StagingFolder)) Directory.Delete(StagingFolder, true);
        Directory.CreateDirectory(StagingFolder);

        CopySourceFiles(new DirectoryInfo(SourceRepo), new DirectoryInfo(StagingFolder));

        Console.WriteLine($"\n✅ Done! Files are ready at: {StagingFolder}");
        Console.WriteLine("👉 Now, in AnythingLLM, just drag the CONTENT of the Staging Folder into the workspace.");
    }

    static void CopySourceFiles(DirectoryInfo source, DirectoryInfo target)
    {
        foreach (DirectoryInfo dir in source.GetDirectories())
        {
            if (ExcludedDirs.Contains(dir.Name, StringComparer.OrdinalIgnoreCase)) continue;
            CopySourceFiles(dir, target.CreateSubdirectory(dir.Name));
        }

        foreach (FileInfo file in source.GetFiles())
        {
            if (AllowedExtensions.Contains(file.Extension.ToLower()))
            {
                file.CopyTo(Path.Combine(target.FullName, file.Name), true);
                Console.Write("."); // Progress indicator
            }
        }
    }
}
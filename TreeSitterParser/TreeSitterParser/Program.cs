using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using TreeSitter;

class Program
{
    // --- JSON DTOs for LLM API ---
    public class Message
    {
        [JsonPropertyName("role")] public string Role { get; set; }
        [JsonPropertyName("content")] public string Content { get; set; }
    }

    public class LlmRequest
    {
        [JsonPropertyName("model")] public string Model { get; set; } = "gemma-4-31b";
        [JsonPropertyName("temperature")] public double Temperature { get; set; } = 0.2;
        [JsonPropertyName("messages")] public List<Message> Messages { get; set; } = new();
    }

    public class LlmResponse
    {
        [JsonPropertyName("choices")] public List<Choice> Choices { get; set; } = new();
    }

    public class Choice
    {
        [JsonPropertyName("message")] public Message Message { get; set; }
    }

    // --- Shared HttpClient for performance ---
    private static readonly HttpClient httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };

    static List<string> ResolveTargetFiles(string[] args, out bool isConcise, out bool classesOnly, out bool isSummarize)
    {
        isConcise = false;
        classesOnly = false;
        isSummarize = false;
        bool isUtilsMode = false;
        string utilsDir = string.Empty;

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--concise") isConcise = true;
            else if (args[i] == "--classesOnly") classesOnly = true;
            else if (args[i] == "--summarize") isSummarize = true;
            else if (args[i] == "--utils" && i + 1 < args.Length)
            {
                isUtilsMode = true;
                utilsDir = args[++i];
            }
        }

        // Enforce constraint: --summarize requires --classesOnly
        if (isSummarize && !classesOnly)
        {
            Console.WriteLine("Warning: --summarize requires --classesOnly to be enabled. Disabling summarization.");
            isSummarize = false;
        }

        var filesToProcess = new List<string>();

        if (isUtilsMode)
        {
            Console.WriteLine($"[Utils Mode] Scanning directory: {utilsDir}");
            if (Directory.Exists(utilsDir))
            {
                var utilityHeaders = Directory.EnumerateFiles(utilsDir, "*.*", SearchOption.AllDirectories)
                    .Where(f => f.EndsWith(".h") || f.EndsWith(".hpp"))
                    .Where(f =>
                    {
                        string fileName = Path.GetFileName(f).ToLower();
                        return fileName.Contains("utils") || fileName.Contains("utilities");
                    });
                filesToProcess.AddRange(utilityHeaders);
            }
            else
            {
                Console.Error.WriteLine($"Error: Target utils directory not found: {utilsDir}");
            }
            return filesToProcess.Distinct().ToList();
        }

        if (File.Exists("dirNames.txt"))
        {
            string[] targetDirectories = File.ReadAllLines("dirNames.txt");
            foreach (string rawLine in targetDirectories)
            {
                string cleanDir = rawLine.Trim().Trim('"').Trim();
                if (string.IsNullOrWhiteSpace(cleanDir) || !Directory.Exists(cleanDir)) continue;
                var headers = Directory.EnumerateFiles(cleanDir, "*.*", SearchOption.AllDirectories)
                    .Where(f => f.EndsWith(".h") || f.EndsWith(".hpp"));
                filesToProcess.AddRange(headers);
            }
        }
        else if (File.Exists("fileNames.txt"))
        {
            string[] targetFiles = File.ReadAllLines("fileNames.txt");
            foreach (string rawLine in targetFiles)
            {
                string cleanFile = rawLine.Trim().Trim('"').Trim();
                if (string.IsNullOrWhiteSpace(cleanFile) || !File.Exists(cleanFile)) continue;
                if (cleanFile.EndsWith(".h") || cleanFile.EndsWith(".hpp"))
                {
                    filesToProcess.Add(cleanFile);
                }
            }
        }

        return filesToProcess.Distinct().ToList();
    }

    static async Task<string> SummarizeFileAsync(string fileContent)
    {
        try
        {
            var requestBody = new LlmRequest
            {
                Messages = new List<Message>
                {
                    new Message {
                        Role = "system",
                        Content = "You are a helpful assistant"
                    },
                    new Message {
                        Role = "user",
                        Content = $"Provide a short summary of this file. Don't think too long. Just a few sentences:\n\n```\n{fileContent}\n```" +
                        "\n. Every word is expensive. Avoid repetitive filler phrases such as This header file declares the `FabricationHeadUtils` class." +
                        " We already know the name of the file. You can just say FabricationHeadUtils class. Pack as much information into every single word as possible. " +
                        " Make information density the highest for the few sentences you have to work with."
                    }
                }
            };

            var jsonContent = new StringContent(
                JsonSerializer.Serialize(requestBody),
                Encoding.UTF8,
                "application/json"
            );

            var response = await httpClient.PostAsync("http://127.0.0.1:8080/v1/chat/completions", jsonContent);

            if (response.IsSuccessStatusCode)
            {
                var responseJson = await response.Content.ReadAsStringAsync();
                var result = JsonSerializer.Deserialize<LlmResponse>(responseJson);
                return result?.Choices?[0]?.Message?.Content ?? "[Summary generation failed]";
            }
            else
            {
                Console.Error.WriteLine($"LLM API Error: {response.StatusCode}");
                return "";
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"LLM Summarization Error: {ex.Message}");
            return "";
        }
    }

    static void Main(string[] args)
    {
        List<string> headerFiles = ResolveTargetFiles(args, out bool isConcise, out bool classesOnly, out bool isSummarize);

        if (headerFiles.Count == 0)
        {
            Console.Error.WriteLine("Error: No configuration files found or no valid header paths resolved. Terminating.");
            return;
        }

        if (classesOnly) Console.WriteLine("Classes only mode");
        if (isSummarize) Console.WriteLine("LLM Summarization mode enabled");

        Console.WriteLine($"\nFound {headerFiles.Count} target header files. Starting Tree-Sitter analysis pass...\n");

        var skeletonBuilder = new StringBuilder();
        skeletonBuilder.AppendLine(isConcise ? "# Utility Map (Concise)" : "# Project Skeleton Map\n");

        using var language = new Language("cpp");
        using var parser = new Parser(language);

        string queryTemplate = isConcise
            ? @"(class_specifier name: (_) @class.name body: (_)) (function_declarator) @func.decl"
            : @"(namespace_definition name: (_) @namespace.name) (class_specifier name: (_) @class.name body: (_)) (struct_specifier name: (_) @struct.name body: (_)) (function_declarator) @func.decl";

        using var query = new Query(language, queryTemplate);
        using var cursor = new QueryCursor();

        // Regex pattern to find upper-case API macros (e.g., CORE_API, RENDERER_API, FX_API_V2)
        Regex apiMacroRegex = new Regex(@"\b[A-Z_][A-Z0-9_]*_API\b", RegexOptions.Compiled);

        foreach (var file in headerFiles)
        {
            string sourceText = File.ReadAllText(file);

            // 1. Get LLM Summary if enabled
            string summary = string.Empty;
            if (isSummarize)
            {
                Console.Write($"[Summarizing] {Path.GetFileName(file)}... ");
                summary = SummarizeFileAsync(sourceText).GetAwaiter().GetResult();

                Console.WriteLine(summary);

                Console.WriteLine("Done.");
            }

            // 2. Write File Header & Summary (Moved outside match loop to include files with no classes)
            skeletonBuilder.AppendLine($"### File: {file}");
            if (!string.IsNullOrEmpty(summary))
            {
                // Clean up excessive newlines in summary for cleaner MD
                string cleanSummary = Regex.Replace(summary, @"\n{3,}", "\n\n");
                skeletonBuilder.AppendLine($"> {cleanSummary}");
                skeletonBuilder.AppendLine();
            }

            // 3. Parse Structure
            string cleanSource = apiMacroRegex.Replace(sourceText, string.Empty);
            using var tree = parser.Parse(cleanSource);
            if (tree?.RootNode == null) continue;

            cursor.Execute(query, tree.RootNode);
            foreach (var match in cursor.Matches)
            {
                foreach (var capture in match.Captures)
                {
                    string nodeText = capture.Node.Text;
                    nodeText = string.Join(" ", nodeText.Split(new[] { ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries));

                    if (capture.Name == "class.name" || capture.Name == "struct.name")
                    {
                        string keyword = capture.Name.Split('.')[0].ToUpper();
                        skeletonBuilder.AppendLine(isConcise ? $"{keyword}: {nodeText}" : $"  * {keyword}: {nodeText}");
                    }
                    else if ((capture.Name == "method.decl" || capture.Name == "func.decl") && !classesOnly)
                    {
                        string cleanDecl = nodeText.TrimEnd('{').TrimEnd(';').Trim();
                        skeletonBuilder.AppendLine(isConcise ? $"  - {cleanDecl}" : $"    - {cleanDecl}");
                    }
                }
            }

            // Add a spacer between files
            skeletonBuilder.AppendLine();
        }

        if (headerFiles.Count > 0)
        {
            File.WriteAllText("REPO_SKELETON.md", skeletonBuilder.ToString());
            Console.WriteLine("\nSkeleton generated successfully in REPO_SKELETON.md!");
        }
    }
}

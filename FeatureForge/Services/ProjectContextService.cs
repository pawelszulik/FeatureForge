using System.IO;
using Microsoft.Extensions.Configuration;

namespace FeatureForge.Services;

public class ProjectContextService
{
    private const int MaxContextLength = 20_000;

    private string _docsPath;

    public ProjectContextService(IConfiguration config)
    {
        _docsPath = config["Docs:Path"] ?? string.Empty;
    }

    public void SetDocsPath(string docsPath) => _docsPath = docsPath;

    public async Task<string> LoadContextAsync(string? moduleName = null)
    {
        if (string.IsNullOrWhiteSpace(_docsPath) || !Directory.Exists(_docsPath))
            return string.Empty;

        // Collect search paths: module subdir first (priority), then root (non-recursive)
        var files = new List<string>();

        // Module subdir — find case-insensitive, supports numeric prefixes like "1. skladziki"
        if (!string.IsNullOrWhiteSpace(moduleName))
        {
            var moduleDir = Directory.GetDirectories(_docsPath)
                .FirstOrDefault(d => Path.GetFileName(d)
                    .Contains(moduleName, StringComparison.OrdinalIgnoreCase));

            if (moduleDir != null)
                files.AddRange(Directory.GetFiles(moduleDir, "*.md", SearchOption.AllDirectories));
        }

        // Root files only (non-recursive) — appended after module files
        files.AddRange(Directory.GetFiles(_docsPath, "*.md", SearchOption.TopDirectoryOnly));

        if (files.Count == 0)
            return string.Empty;

        var parts = new List<string>();
        int totalLength = 0;

        foreach (var file in files.OrderBy(f => f))
        {
            var relativePath = Path.GetRelativePath(_docsPath, file).Replace('\\', '/');
            var content = await File.ReadAllTextAsync(file);
            var section = $"## {relativePath}\n{content}";

            if (totalLength + section.Length > MaxContextLength)
            {
                var remaining = MaxContextLength - totalLength;
                if (remaining > 100)
                    parts.Add(section[..remaining] + "\n[...obcięto]");
                break;
            }

            parts.Add(section);
            totalLength += section.Length;
        }

        return string.Join("\n\n", parts);
    }

    public async Task SaveDocsUpdateAsync(Dictionary<string, string> updates)
    {
        if (string.IsNullOrWhiteSpace(_docsPath))
            throw new InvalidOperationException("Docs:Path nie jest skonfigurowane w appsettings.");

        Directory.CreateDirectory(_docsPath);

        foreach (var (fileName, content) in updates)
        {
            var safeName = Path.GetFileNameWithoutExtension(fileName);
            var proposedName = $"{safeName}.proposed.md";
            var fullPath = Path.Combine(_docsPath, proposedName);
            await File.WriteAllTextAsync(fullPath, content);
        }
    }
}

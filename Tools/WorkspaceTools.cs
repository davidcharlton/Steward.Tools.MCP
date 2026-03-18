using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using StewardMcp.Services;

namespace StewardMcp.Tools;

[McpServerToolType]
public class WorkspaceTools
{
    private readonly WorkspaceService _ws;

    public WorkspaceTools(WorkspaceService ws)
    {
        _ws = ws;
    }

    [McpServerTool]
    [Description("Get a compact directory tree of the workspace.")]
    public string WorkspaceTree(
        [Description("Relative path (empty for root)")] string path = "",
        [Description("Max depth to traverse (default 3)")] int maxDepth = 3)
    {
        return _ws.Tree(path, maxDepth);
    }

    [McpServerTool]
    [Description("List files in a workspace directory.")]
    public string WorkspaceList(
        [Description("Relative path (empty for root)")] string path = "",
        [Description("Include subdirectories")] bool recursive = false)
    {
        var items = _ws.List(path, recursive);
        return JsonSerializer.Serialize(new { path, count = items.Count, items });
    }

    [McpServerTool]
    [Description("Read lines from a file in the workspace.")]
    public string WorkspaceRead(
        [Description("Relative file path")] string path,
        [Description("Starting line number (default 1)")] int startLine = 1,
        [Description("Max lines to read (default 200)")] int maxLines = 200)
    {
        try
        {
            var (content, sha256) = _ws.ReadLines(path, startLine, maxLines);
            return JsonSerializer.Serialize(new { path, startLine, sha256, content });
        }
        catch (FileNotFoundException)
        {
            return JsonSerializer.Serialize(new { error = $"File not found: {path}" });
        }
    }

    [McpServerTool]
    [Description("Create or overwrite a text file in the workspace. The file will be automatically indexed for semantic search.")]
    public async Task<string> WorkspaceWrite(
        [Description("Relative file path")] string path,
        [Description("File content")] string content,
        [Description("Must be true to overwrite existing files")] bool overwrite = false)
    {
        var result = await _ws.WriteAsync(path, content, overwrite);
        return result;
    }

    [McpServerTool]
    [Description("Edit specific lines in a workspace file. Use sha256 from workspace_read for safe concurrent editing.")]
    public async Task<string> WorkspacePatch(
        [Description("Relative file path")] string path,
        [Description("First line to replace (1-based)")] int startLine,
        [Description("Last line to replace (inclusive)")] int endLine,
        [Description("Replacement text")] string replacement,
        [Description("Expected SHA256 of the file (from workspace_read) for conflict detection")] string? expectedSha256 = null)
    {
        var result = await _ws.PatchAsync(path, startLine, endLine, replacement, expectedSha256);
        return result;
    }

    [McpServerTool]
    [Description("Search for text in workspace files.")]
    public string WorkspaceSearch(
        [Description("Search text")] string query,
        [Description("Relative path to search in (empty for all)")] string path = "",
        [Description("Max results (default 20)")] int maxResults = 20)
    {
        var matches = _ws.Search(query, path, maxResults);
        return JsonSerializer.Serialize(new { query, count = matches.Count, matches });
    }

    [McpServerTool]
    [Description("Delete a file from the workspace.")]
    public async Task<string> WorkspaceDelete(
        [Description("Relative file path")] string path)
    {
        var result = await _ws.DeleteAsync(path);
        return result;
    }
}

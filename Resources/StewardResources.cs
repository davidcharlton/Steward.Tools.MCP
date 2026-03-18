using System.ComponentModel;
using ModelContextProtocol.Server;
using StewardMcp.Formation;

namespace StewardMcp.Resources;

[McpServerResourceType]
public class StewardResources
{
    private readonly Canon _canon;
    private readonly DossierBuilder _dossiers;

    public StewardResources(Canon canon, DossierBuilder dossiers)
    {
        _canon = canon;
        _dossiers = dossiers;
    }

    [McpServerResource(UriTemplate = "steward://seed")]
    [Description("The foundational identity and formation text (the Seed). Read this at the start of every conversation to understand who you are as a steward.")]
    public string GetSeed()
    {
        return _canon.GetCanonContext();
    }

    [McpServerResource(UriTemplate = "steward://context/{threadId}")]
    [Description("Assembled formation context for a conversation thread. Includes master dossier and thread dossier. Read this to load working memory for a conversation.")]
    public async Task<string> GetContext(
        [Description("Thread ID")] string threadId)
    {
        return await _dossiers.BuildContextSystemPromptAsync(threadId);
    }
}

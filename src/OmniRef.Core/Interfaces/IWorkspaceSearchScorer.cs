using OmniRef.Core.Models;

namespace OmniRef.Core.Interfaces;

/// <summary>
/// Scores a normalized query against a workspace search document.
/// </summary>
public interface IWorkspaceSearchScorer
{
    SearchMatch Score(string normalizedQuery, SearchDocument document);
}

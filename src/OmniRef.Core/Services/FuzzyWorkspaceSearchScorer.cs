using System.Globalization;
using OmniRef.Core.Interfaces;
using OmniRef.Core.Models;
using Raffinert.FuzzySharp;
using Raffinert.FuzzySharp.PreProcess;

namespace OmniRef.Core.Services;

public sealed class FuzzyWorkspaceSearchScorer : IWorkspaceSearchScorer
{
    private const int MinimumFuzzyScore = 60;

    public SearchMatch Score(string normalizedQuery, SearchDocument document)
    {
        ArgumentNullException.ThrowIfNull(normalizedQuery);
        ArgumentNullException.ThrowIfNull(document);

        var query = normalizedQuery.Trim();
        if (query.Length == 0)
        {
            return new SearchMatch(true, 0);
        }

        var minimumScore = MinimumScoreFor(query);
        var matched = false;
        var bestScore = 0d;
        foreach (var field in document.Fields)
        {
            var rawScore = ScoreField(query, field.Value);
            if (rawScore >= minimumScore)
            {
                matched = true;
            }

            var weightedScore = rawScore / 100d * FieldWeight(field.Kind);
            bestScore = Math.Max(bestScore, weightedScore);
        }

        return new SearchMatch(matched, bestScore);
    }

    private static int ScoreField(string query, string candidate)
    {
        if (CultureInfo.CurrentCulture.CompareInfo.IndexOf(
                candidate,
                query,
                CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0)
        {
            return 100;
        }

        return Fuzz.WeightedRatio(query, candidate, StringPreprocessor.Full);
    }

    private static int MinimumScoreFor(string query) => query.Length switch
    {
        1 => 100,
        2 => 80,
        3 => 70,
        _ => MinimumFuzzyScore
    };

    private static double FieldWeight(SearchFieldKind kind) => kind switch
    {
        SearchFieldKind.Title => 1.00,
        SearchFieldKind.FileName => 0.95,
        SearchFieldKind.Tag => 0.90,
        SearchFieldKind.Url => 0.85,
        SearchFieldKind.DisplayHost => 0.85,
        SearchFieldKind.AltText => 0.80,
        SearchFieldKind.Extension => 0.75,
        SearchFieldKind.Text => 0.70,
        SearchFieldKind.RelativePath => 0.60,
        SearchFieldKind.AbsolutePath => 0.55,
        _ => 0
    };
}

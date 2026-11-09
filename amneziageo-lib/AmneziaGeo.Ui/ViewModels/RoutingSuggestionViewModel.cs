namespace AmneziaGeo.Ui.ViewModels;

/// <summary>
/// One suggestion under the rule input: the token a tap adds, what it covers and its type badge.
/// </summary>
internal sealed class RoutingSuggestionViewModel
{
    /// <summary>
    /// ctor
    /// </summary>
    public RoutingSuggestionViewModel(string token)
        : this(token, RuleToken.Describe(token))
    {
    }

    /// <summary>
    /// ctor
    /// </summary>
    public RoutingSuggestionViewModel(string token, string description, bool isWarning = false)
    {
        Token = token;
        Description = description;
        Badge = RuleToken.Badge(token);
        IsWarning = isWarning;
    }

    /// <summary>
    /// The rule token added when the row is picked.
    /// </summary>
    public string Token { get; }

    /// <summary>
    /// What the token covers.
    /// </summary>
    public string Description { get; }

    /// <summary>
    /// The token type, shown at the right of the row.
    /// </summary>
    public string Badge { get; }

    /// <summary>
    /// Marks a missing path.
    /// </summary>
    public bool IsWarning { get; }
}

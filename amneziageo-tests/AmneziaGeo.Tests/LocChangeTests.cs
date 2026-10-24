using System.ComponentModel;
using AmneziaGeo.Localization;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// Tests that switch the language of the whole process run alone.
/// </summary>
[CollectionDefinition("Culture", DisableParallelization = true)]
public sealed class CultureCollection;

/// <summary>
/// The window hears of a language change only when the language changes: the language in use raises nothing, another
/// one raises the change once.
/// </summary>
[Collection("Culture")]
public sealed class LocChangeTests
{
    [Fact]
    public void TheLanguageInUse_RaisesNothing()
    {
        var token = Loc.Instance.Culture.TwoLetterISOLanguageName;
        Loc.Instance.SetCulture(token);

        var raised = Count(() =>
        {
            Loc.Instance.SetCulture(token);
            Loc.Instance.ApplyStartupCulture(token);
        });

        Assert.Equal((0, 0), raised);
    }

    [Fact]
    public void AnotherLanguage_RaisesTheChangeOnce()
    {
        var held = Loc.Instance.Culture.TwoLetterISOLanguageName;
        var other = held == "ru" ? "en" : "ru";
        try
        {
            var raised = Count(() => Loc.Instance.SetCulture(other));

            Assert.Equal((1, 1), raised);
            Assert.Equal(other, Loc.Instance.Culture.TwoLetterISOLanguageName);
        }
        finally
        {
            Loc.Instance.SetCulture(held);
        }
    }

    // Counts the language changes and the binding refreshes an action raises.
    private static (int Changes, int Bindings) Count(Action action)
    {
        var changes = 0;
        var bindings = 0;
        void OnChange() => changes++;
        void OnBinding(object? sender, PropertyChangedEventArgs e) => bindings++;

        Loc.Instance.CultureChanged += OnChange;
        Loc.Instance.PropertyChanged += OnBinding;
        try
        {
            action();
        }
        finally
        {
            Loc.Instance.CultureChanged -= OnChange;
            Loc.Instance.PropertyChanged -= OnBinding;
        }

        return (changes, bindings);
    }
}

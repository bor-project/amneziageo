using System;
using System.Collections.Generic;
using System.Globalization;
using AmneziaGeo.Localization;
using AmneziaGeo.Ui.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AmneziaGeo.Ui.ViewModels;

/// <summary>
/// Один из текстов условий на языке окна: экран первого запуска и страница из «О приложении».
/// </summary>
internal sealed partial class LegalViewModel : ViewModelBase
{
    private readonly UiPreferences _prefs;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTerms))]
    [NotifyPropertyChangedFor(nameof(IsPrivacy))]
    [NotifyPropertyChangedFor(nameof(AcceptedLine))]
    [NotifyPropertyChangedFor(nameof(HasAcceptedLine))]
    private string _document = LegalTexts.Terms;

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasVersion))]
    [NotifyPropertyChangedFor(nameof(FooterLine))]
    private string _versionLine = string.Empty;

    [ObservableProperty]
    private IReadOnlyList<LegalBlock> _body = [];

    /// <summary>
    /// ctor
    /// </summary>
    public LegalViewModel(UiPreferences prefs)
    {
        _prefs = prefs;
        Load();
        Loc.Instance.CultureChanged += Load;
    }

    /// <summary>
    /// Открыты ли условия использования.
    /// </summary>
    public bool IsTerms => Document == LegalTexts.Terms;

    /// <summary>
    /// Открыта ли конфиденциальность.
    /// </summary>
    public bool IsPrivacy => Document == LegalTexts.Privacy;

    /// <summary>
    /// Есть ли у текста строка версии.
    /// </summary>
    public bool HasVersion => VersionLine.Length > 0;

    /// <summary>
    /// Строка под текстом экрана первого запуска: версия и лицензия.
    /// </summary>
    public string FooterLine => HasVersion ? VersionLine + " · GPL-3.0" : "GPL-3.0";

    /// <summary>
    /// Когда приняты условия, для условий и конфиденциальности.
    /// </summary>
    public string AcceptedLine => (IsTerms || IsPrivacy) && LegalTexts.Accepted(_prefs) && AcceptedOn() is { } day
        ? Loc.Instance.Get("Terms_AcceptedOn", day.ToString("d", Loc.Instance.Culture))
        : string.Empty;

    /// <summary>
    /// Есть ли строка о принятии.
    /// </summary>
    public bool HasAcceptedLine => AcceptedLine.Length > 0;

    /// <summary>
    /// Перечитывает строку о принятии.
    /// </summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(AcceptedLine));
        OnPropertyChanged(nameof(HasAcceptedLine));
    }

    [RelayCommand]
    private void Show(string document)
    {
        Document = document;
    }

    partial void OnDocumentChanged(string value)
    {
        Load();
    }

    private void Load()
    {
        var text = LegalTexts.Load(Document, Loc.Instance.Culture);
        Title = text.Title;
        VersionLine = text.Version;
        Body = text.Body;
        Refresh();
    }

    // День принятия из настроек.
    private DateTime? AcceptedOn() =>
        DateTime.TryParseExact(_prefs.AcceptedTermsOn, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            ? day
            : null;
}

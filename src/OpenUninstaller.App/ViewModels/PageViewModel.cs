using System.ComponentModel;
using OpenUninstaller.App.Infrastructure;
using OpenUninstaller.Core.Localization;

namespace OpenUninstaller.App.ViewModels;

public abstract class PageViewModel : ObservableObject, IPageViewModel
{
    private bool _isBusy;
    private Func<string>? _statusMessage;

    protected PageViewModel()
    {
        PropertyChangedEventManager.AddHandler(LocalizationService.Instance, OnLanguageChanged,
            nameof(LocalizationService.CurrentLanguage));
    }

    public bool IsBusy
    {
        get => _isBusy;
        protected set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(IsNotBusy));
            }
        }
    }

    public bool IsNotBusy => !IsBusy;

    public string StatusMessage => _statusMessage?.Invoke() ?? string.Empty;

    protected void SetStatus(string key, params object?[] arguments) =>
        SetStatus(() => UiText.F(key, arguments));

    protected void SetStatus(Func<string> message)
    {
        _statusMessage = message;
        OnPropertyChanged(nameof(StatusMessage));
    }

    private void OnLanguageChanged(object? sender, PropertyChangedEventArgs e) => OnPropertyChanged(string.Empty);

    public virtual void Activate()
    {
    }
}

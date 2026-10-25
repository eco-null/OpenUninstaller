using OpenUninstaller.App.Infrastructure;

namespace OpenUninstaller.App.ViewModels;

public abstract class PageViewModel : ObservableObject, IPageViewModel
{
    private bool _isBusy;
    private string _statusMessage = string.Empty;

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

    public string StatusMessage
    {
        get => _statusMessage;
        protected set => SetProperty(ref _statusMessage, value);
    }

    public virtual void Activate()
    {
    }
}

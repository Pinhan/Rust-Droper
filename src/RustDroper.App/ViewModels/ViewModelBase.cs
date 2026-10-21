using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using RustDroper.Core.Localization;

namespace RustDroper.App.ViewModels;

public abstract class ViewModelBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(name);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>
    /// Arayüz dili değişince <see cref="OnLanguageChanged"/> çağrılır (zayıf abonelik: görünüm modeli atılınca olay onu tutmaz).
    /// Uzun ömürlü görünüm modelleri oluşturucuda bir kez çağırır.
    /// </summary>
    protected void ObserveLanguage()
    {
        var weak = new WeakReference<ViewModelBase>(this);
        Action? handler = null;
        handler = () =>
        {
            if (weak.TryGetTarget(out var target))
            {
                target.OnLanguageChanged();
            }
            else
            {
                Loc.LanguageChanged -= handler;
            }
        };
        Loc.LanguageChanged += handler;
    }

    /// <summary>Dil değişti: yerelleştirilmiş özellikleri yeniden oku. Varsayılan: tüm özellikler için bildirim.</summary>
    protected virtual void OnLanguageChanged() => OnPropertyChanged(string.Empty);
}

/// <summary>Senkron/asenkron komut. Asenkron çalışırken yeniden girişi engeller; istisnaları <see cref="Failed"/>'a iletir (UI'ı çökertmez).</summary>
public sealed class RelayCommand : ICommand
{
    private readonly Func<object?, Task> _execute;
    private readonly Func<object?, bool>? _canExecute;
    private bool _running;

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
        : this(
            p =>
            {
                execute(p);
                return Task.CompletedTask;
            },
            canExecute)
    {
    }

    public RelayCommand(Func<object?, Task> executeAsync, Func<object?, bool>? canExecute = null)
    {
        ArgumentNullException.ThrowIfNull(executeAsync);
        _execute = executeAsync;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    /// <summary>Komut bir istisnayla sonlanırsa tetiklenir (ör. günlüğe yazmak için).</summary>
    public event Action<Exception>? Failed;

    public bool CanExecute(object? parameter) => !_running && (_canExecute?.Invoke(parameter) ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        _running = true;
        RaiseCanExecuteChanged();
        try
        {
            await _execute(parameter).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Failed?.Invoke(ex);
        }
        finally
        {
            _running = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

namespace JoyPro.Gui.Services;

// Shows the overlay window (Views/OverlayWindow); the view model only says when.
public interface IOverlayPresenter
{
    void Show(object dataContext);

    void Hide();
}

using System.IO;
using Content.Client.Eui;
using Content.Shared._Stalker_EN.Camera;
using Content.Shared.Eui;
using Robust.Client.Graphics;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Content.Client._Stalker_EN.Camera;

/// <summary>
/// Client-side EUI that displays a photo preview for admins.
/// </summary>
public sealed class STAdminPhotoPreviewEui : BaseEui
{
    [Dependency] private readonly IClyde _clyde = default!;

    private STPhotoWindow? _window;

    public override void Opened()
    {
        _window = new STPhotoWindow();
        _window.OpenCentered();
        _window.StartLoading();
        _window.OnClose += () => SendMessage(new CloseEuiMessage());
    }

    public override void HandleState(EuiStateBase state)
    {
        if (state is not STAdminPhotoPreviewEuiState photoState)
            return;

        if (_window == null)
            return;

        if (photoState.ImageData.Length == 0)
        {
            _window.ShowUnavailable();
            return;
        }

        try
        {
            using var stream = new MemoryStream(photoState.ImageData);
            using var image = Image.Load<Rgba32>(stream);
            var texture = _clyde.LoadTextureFromImage(image);
            _window.SetTexture(texture);
        }
        catch (SixLabors.ImageSharp.ImageFormatException)
        {
            _window.ShowUnavailable();
        }
    }

    public override void Closed()
    {
        _window?.Close();
        _window?.Dispose();
        _window = null;
    }
}

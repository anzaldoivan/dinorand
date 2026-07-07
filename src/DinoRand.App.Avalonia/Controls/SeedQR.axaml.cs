#nullable enable
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using QRCoder;

namespace DinoRand.App
{
    /// <summary>
    /// Avalonia port of the WPF <c>SeedQR</c>. Encodes whatever <see cref="SeedString"/> the host
    /// supplies (we feed it <c>AppSeed.ToString()</c>) as a QR code. Uses QRCoder's
    /// <see cref="PngByteQRCode"/> — no <c>System.Drawing</c> — and decodes the PNG bytes straight
    /// into an <see cref="Bitmap"/>. Translation notes vs the WPF original:
    ///   • <c>DependencyProperty.Register</c> + metadata callback → <see cref="StyledProperty{T}"/>
    ///     + a static <c>Changed.AddClassHandler</c>.
    ///   • <c>BitmapImage</c>/<c>BeginInit</c>/<c>Freeze</c> → <c>new Bitmap(stream)</c> (immutable).
    ///   • <c>RenderOptions.SetBitmapScalingMode(NearestNeighbor)</c> →
    ///     <c>RenderOptions.SetBitmapInterpolationMode(None)</c>.
    /// </summary>
    public partial class SeedQR : UserControl
    {
        public static readonly StyledProperty<string> SeedStringProperty =
            AvaloniaProperty.Register<SeedQR, string>(nameof(SeedString), defaultValue: "");

        public string SeedString
        {
            get => GetValue(SeedStringProperty);
            set => SetValue(SeedStringProperty, value);
        }

        // The image field + InitializeComponent() are generated from the x:Name'd Image in the axaml.
        private Bitmap? _bitmap;

        static SeedQR()
        {
            // Repaint whenever the seed changes (binding or direct set).
            SeedStringProperty.Changed.AddClassHandler<SeedQR>((control, _) => control.UpdateImage());
        }

        public SeedQR()
        {
            InitializeComponent();
            UpdateImage();
        }

        private void UpdateImage()
        {
            var seed = SeedString;
            if (string.IsNullOrEmpty(seed))
                seed = " ";

            var qrGenerator = new QRCodeGenerator();
            var qrCodeData = qrGenerator.CreateQrCode(seed, QRCodeGenerator.ECCLevel.M);
            var qrCode = new PngByteQRCode(qrCodeData);
            var pngBytes = qrCode.GetGraphic(3);

            using var ms = new MemoryStream(pngBytes);
            var bitmap = new Bitmap(ms);          // decodes synchronously; ms can be disposed after
            image.Source = bitmap;
            image.Stretch = Stretch.None;
            RenderOptions.SetBitmapInterpolationMode(image, BitmapInterpolationMode.None);

            // Release the previous frame now that the new one is shown (Avalonia bitmaps are IDisposable).
            _bitmap?.Dispose();
            _bitmap = bitmap;
        }
    }
}

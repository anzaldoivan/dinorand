using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QRCoder;

namespace DinoRand.App
{
    /// <summary>
    /// Interaction logic for SeedQR.xaml
    /// </summary>
    /// <remarks>
    /// Adapted from BioRand's SeedQR: instead of encoding a RandoConfig it encodes any
    /// <see cref="SeedString"/> the host supplies (we feed it <see cref="AppSeed.ToString"/>).
    /// Uses QRCoder's <see cref="PngByteQRCode"/> so there's no System.Drawing dependency.
    /// </remarks>
    public partial class SeedQR : UserControl
    {
        public static readonly DependencyProperty SeedStringProperty =
            DependencyProperty.Register(
                nameof(SeedString),
                typeof(string),
                typeof(SeedQR),
                new PropertyMetadata("", SeedString_Changed));

        public string SeedString
        {
            get => (string)GetValue(SeedStringProperty);
            set => SetValue(SeedStringProperty, value);
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

            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.StreamSource = new MemoryStream(pngBytes);
            bi.EndInit();
            bi.Freeze();

            image.Source = bi;
            image.Stretch = Stretch.None;
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
        }

        private static void SeedString_Changed(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            (d as SeedQR)?.UpdateImage();
        }
    }
}

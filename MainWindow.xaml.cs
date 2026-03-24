using Primera.Print;
using Primera.Print.Modules;
using Primera.Print.PRN;

using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Printing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Timers;
using System.Windows;
using System.Windows.Media.Imaging;

using UnitsNet;

namespace DemoApp
{
    /// <summary>
    /// This is a simple demo app intended to showcase the most basic capabilities of Primera.Print.
    /// </summary>
    public partial class MainWindow : Window
    {
        public MainWindow()
        {

            TraceSource source = new TraceSource("DemoApp");

            var registry = new PrimeraRegistry();
            // Create the connection management object.
            // This is done using a builder that chains operations together for flexible definition
            // The registrations we include determine the list of printer types that will be enumerated
            //
            // Our connection manager will look at the Windows Registry, gathering the list of all printers of the correct model
            // and assign connection and state-caching logic to them so we can update status and send commands.
            Manager = PrinterCollection.Builder.FromRegistrations(registry.LX610)
                // Should we enumerate print queues from the network?
                // .EnableNetworkPrinters();  // Optionally comment this line to only enable USB connected printers

                // Do we want to enumerate printers that are not installed to a print queue?
                // .WithRefreshType(RefreshTypes.AllPrinters);  // Optionally comment this line out to only enumerate installed printers

                // Primera.Print flexibly supports tracing from your application.
                // Add a TraceSource that you get from anywhere.
                // Ours is defined in the DemoApp config file.
                .WithTracing(new TraceSource("DemoApp"))

                // Finally resolve the builder to create our connection manager.
                .Build();

            // To use manager to create printers we must first register the type we wish to use.
            // In this case we want all default primera printers that are supported.
            // Manager.RegisterDefaultPrimeraPrinters();

            Loaded += MainWindow_Loaded;

            InitializeComponent();
            SetupAutoPrint();
        }

        private IPrinterCollection Manager { get; }

        /// <summary>
        /// We will be getting the first printer from the Manager
        /// </summary>
        private IPrimeraPrinter MyPrinter => Manager.GetCollection().FirstOrDefault();
        // Ändra dessa stigar så de passar din dator
        // FREDRIK MIRAGE DATOR private string _watchPath = @"C:\Volumes\mirage\mirage\BAT\Boutique Festival Velo Shift\DoodlesServerSetup\utskrifter";
        // DTM PRINT COMPUTER:
        private string _watchPath = @"C:\Users\DTM Print\Desktop\Sticker Station\1. Start Here\utskrifter";

        private string _finishedPath = @"C:\PrintServer\Finished";
        private string _currentFileToPrint = "";

        private void SetupAutoPrint()
        {
            // Skapa mapparna om de inte finns
            Directory.CreateDirectory(_watchPath);
            Directory.CreateDirectory(_finishedPath);

            FileSystemWatcher watcher = new FileSystemWatcher(_watchPath);
            watcher.Filter = "*.png"; // Vi letar efter PNG-filer från Unity
            watcher.EnableRaisingEvents = true;

            // Detta körs när en fil landar i mappen
            watcher.Created += (s, e) =>
            {
                // En kort paus så att filen hinner skrivas klart av Windows/Nätverket
                System.Threading.Thread.Sleep(700);

                _currentFileToPrint = e.FullPath;

                // Vi måste gå tillbaka till "Main Thread" för att få skriva ut
                Application.Current.Dispatcher.Invoke(() =>
                {
                    StartAutomaticPrint(_currentFileToPrint);
                });
            };
        }

        private async void StartAutomaticPrint(string filePath)
        {
            if (MyPrinter == null) return;

            PrintDocument doc = new PrintDocument()
            {
                PrintController = new StandardPrintController(),
                DocumentName = "Unity Auto Print"
            };

            doc.PrinterSettings.PrinterName = MyPrinter.PrinterName;

            // --- STEG 2: DTMs magiska RawKind-inställningar ---
            // Genom att lägga till { RawKind = 0x100 } tvingar vi Windows att acceptera 400x400 (Custom Size)
            PaperSize customSize = new PaperSize("Custom", 400, 400) { RawKind = 0x100 };
            doc.DefaultPageSettings.PaperSize = customSize;

            // Denna rad säkerställer att även papperskällan förstår att det är en anpassad storlek
            doc.DefaultPageSettings.PaperSource.RawKind = 0x100;

            doc.DefaultPageSettings.Margins = new Margins(0, 0, 0, 0);
            // --------------------------------------------------

            // Här sätter vi LX610e-specifika inställningar
            var values = new PrivateDevModeValues()
            {
                Quality = Quality.Best,
                Saturation = 90
            };
            MyPrinter.Print.SetPrivateSettings(doc.PrinterSettings, values);

            // Eventet som faktiskt ritar bilden
            doc.PrintPage += (s, ev) =>
            {
                using (System.Drawing.Image img = System.Drawing.Image.FromFile(filePath))
                {
                    // Vi tvingar bilden att fylla 4x4 tum (10.16 cm)
                    ev.Graphics.DrawImage(img, 0, 0, 400, 400);
                }
            };

            try
            {
                // 1. SKRIV UT BILDEN FÖRST
                doc.Print();

                // 2. PAUS (Mycket viktigt!)
                // Vi ger Windows 3 sekunder att spola in bild-datan till skrivaren
                // innan vi skjuter in kniv-datan. Annars kan de krocka i kön.
                await Task.Delay(3000);

                // 3. SKICKA SKÄRNINGEN (Kniven)
                SendCutToPrinter(MyPrinter.PrinterName);

                // 4. Flytta filen till "Finished" så den inte printas igen
                string fileName = Path.GetFileName(filePath);

                // (Tips: Om du får fel här ibland beror det på att filen redan finns i Finished-mappen. 
                //  Du kan lägga till lite kod som raderar den gamla först om det behövs senare).
                File.Move(filePath, Path.Combine(_finishedPath, fileName));

                Debug.WriteLine("Print Success: " + fileName);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Print Error: " + ex.Message);
            }
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            Title = "Single Printer Demo - " + Assembly.GetExecutingAssembly().GetName().Version.ToString();

            // Initialize our StatusTimer so that we will get status periodically
            StatusTimer.Elapsed += StatusTimer_Elapsed;
            StatusTimer.Start();

            UpdateVariables(true);
        }

        #region Polling for status

        // The printer requires some time to gather status.
        // Any polling below a frequency of 500 ms is not guaranteed to work.
        // Most Primera Applications poll with a frequency of 1000 ms.
        private Timer StatusTimer { get; } = new Timer(1000);

        private async void StatusTimer_Elapsed(object sender, ElapsedEventArgs e)
        {
            // UpdateStatus has potential to take a long time if the port it reads from is blocking.
            // Primera.Print can handle concurrent calls to update status, but we want to avoid forcing that behavior as much as possible

            // To prevent stacking StatusTimer.Elapsed events when UpdateStatus blocks, Stop the timer and restart when we are finished.
            StatusTimer.Stop();
            try
            {
                // We update our status and list of printers simultaneously
                // The manager will gather all the printers and update status when needed
                await Manager.RefreshUsbPrintersAsync();
                Application.Current?.Dispatcher?.Invoke(() => UpdateVariables(false));
            }
            finally
            {
                StatusTimer.Start();
            }
        }

        private void Start_Click(object sender, RoutedEventArgs e)
        {
            StatusTimer.Start();
        }

        private void Stop_Click(object sender, RoutedEventArgs e)
        {
            StatusTimer.Stop();
        }

        #endregion Polling for status

        #region Getting Printer Values

        private void UpdateVariables(bool reloadTextBox)
        {
            // The printer name will usually be the Print Queue associated with our device.
            PrinterName.Text = MyPrinter is null ? "No Printer Found" : MyPrinter.PrinterName;

            // If we have no printer we cannot update the view
            if (MyPrinter is null) return;

            // CurrentState is the action the printer is currently performing.
            CurrentState.Text = MyPrinter.CurrentState.ToString();

            // ErrorList is a collection of all printers currently affecting printer. An empty list if there are none.
            // The "Coerced" errors means that the SDK is interpreting the printer status to generate additional or refine error details
            ErrorList.ItemsSource = MyPrinter.GetCoercedErrors();

            // Most printer values are split into "Modules".
            // This is a sampling of some values that can be found.
            // There are many more, see the full documentation for the full capabilities of each printer.
            RecordNumber.Text = MyPrinter.DeviceInfo.RecordNumber.ToString();
            FirmwareVersion.Text = $"{MyPrinter.CoreFirmware.FirmwareVersion.Version.Major}.{MyPrinter.CoreFirmware.FirmwareVersion.Version.Minor:D2}";

            if (MyPrinter is IHaveInkCartridge printHeadPrinter)
            {
                // For printers that have a single cartridge (e.g. LX500, IP60, LX610, LX600, LX910) we expose the cartridge state to generate the percent remaining
                CartridgeRemaining.Text = $"{printHeadPrinter.CartridgeInfo.Cartridge.CartridgePercentRemaining():p0}";
            }
            else if (MyPrinter is IHaveInkBottleCartridge bottlePrinter)
            {
                // Otherwise, if the printer has bottles external to the printhead, we represent each value separately

                double cyanRemaining = bottlePrinter.CartridgeAndBottleInfo.CyanBottle?.PercentRemaining() ?? 0;
                var magentaRemaining = bottlePrinter.CartridgeAndBottleInfo.MagentaBottle?.PercentRemaining() ?? 0;
                var yellowRemaining = bottlePrinter.CartridgeAndBottleInfo.YellowBottle?.PercentRemaining() ?? 0;
                CartridgeRemaining.Text = $"C:{cyanRemaining:p0}, M:{magentaRemaining:p0}, Y:{yellowRemaining:p0}";
            }
            else
            {
                // And some printers have no cartridge (e.g. Catalyst) so there is no ink remaining to view
                CartridgeRemaining.Visibility = Visibility.Hidden;
            }

            if (MyPrinter is IHavePaperChip chippedMediaPrinter)
            {
                // Some printers (e.g. IP60, LX610, Catalyst) have chipped media and can display the percent remaining
                PaperRemaining.Text = $"{chippedMediaPrinter.PaperChip.MediaPercentRemaining:p0}";
            }
            else
            {
                PaperRemainingLabel.Visibility = Visibility.Hidden;
            }

            // Since we get status every second, we need to conditionally update the Textboxes that can be user edited.
            if (reloadTextBox)
            {
                if (MyPrinter is IHaveAlignment alignmentPrinter)
                {
                    // Printers that have label alignment capabilities will have user settings to adjust
                    // In this case, the setting on the printer represents a length in 300 DPI and is stored using a signed byte
                    LengthResolution<sbyte, Count300> tofOffset = alignmentPrinter.Alignment.UserCutterOffset.Value;
                    LengthResolution<sbyte, Count300> horizontalOffset = alignmentPrinter.Alignment.UserHorizontalOffset.Value;

                    // The SDK takes care of converting from that value to any valid length unit or, if desired, the encoded value directly
                    TopOfForm.Text = tofOffset.Length.Inches.ToString();
                    HorizontalOffset.Text = horizontalOffset.Length.Inches.ToString();
                }
                else
                {
                    TopOfForm.Visibility = Visibility.Hidden;
                    HorizontalOffset.Visibility = Visibility.Hidden;
                }
            }
        }

        #endregion Getting Printer Values

        #region Setting a Printer Value

        private async void SetTopOfForm_Click(object sender, RoutedEventArgs e)
        {
            // Our printer must have alignment settings to assign new values
            var alignmentPrinter = MyPrinter as IHaveAlignment;
            if (alignmentPrinter is null) return;

            try
            {
                // To finally set the printer value, we get the same property definition that we received the value from earlier and send a command to the printer
                // For this setting, the property we are accessing is mapped from the domain of integers to the domain of length units, represented by a 300 DPI conversion.
                double tofValue = double.Parse(TopOfForm.Text);
                Length parsedLength = Length.FromInches(tofValue);
                bool success = await alignmentPrinter.Alignment.UserCutterOffset.SetValueAsync(Resolutions.SByteLength<Count300>(parsedLength));
            }
            catch (FormatException)
            {
                // We haven't done any special logic to the Text Boxes, so they can give us ill-formed input.
                // If we do get ill-formed input, reload the values from the printer.
                UpdateVariables(true);
            }
        }

        // Since our final operation will be to communicate with the printer, this is an async method.
        // `async Task` should be used outside of event callbacks like this one.
        private async void SetHorizontalOffset_Click(object sender, RoutedEventArgs e)
        {
            var alignmentPrinter = MyPrinter as IHaveAlignment;
            if (alignmentPrinter is null) return;

            try
            {
                // The SDK will compute and coerce the final integer value given an arbitrary double value in any unit using UnitsNet to convert from Unit to any length definition
                // then finally coercing that unit value such that it aligns with the granularity of the underlying data-store.
                double horzValue = double.Parse(HorizontalOffset.Text);
                Length parsedLength = Length.FromInches(horzValue);
                bool success = await alignmentPrinter.Alignment.UserHorizontalOffset.SetValueAsync(Resolutions.SByteLength<Count300>(parsedLength));
            }
            catch (FormatException)
            {
                UpdateVariables(true);
            }
        }

        #endregion Setting a Printer Value

        #region Printing an Image

        private async void SendPrint_Click(object sender, RoutedEventArgs e)
        {
            // Some of the most important prints are included with the SDK in a separate assembly called "Primera.Print.PRN"
            // This is a large, and optional DLL. Excluding it from your project will no have no negative consequences,
            // but it will not include the embedded test prints.

            // The Test offsets is one such print, and differs from printer to printer.

            // In general, the prints available are supported on a Printer-by-Printer basis.
            // Any printer that supports IHaveAlignment will support the TestOffsetsPrint, in particular.
            if (!TestOffsetsPrint.IsSupported(MyPrinter)) return;

            // Once a print is created, we can send it to the printer. This can be done multiple times on the same object
            // Multiple simultaneous attempts, however, is not supported writes will likely fail.
            var myPrint = TestOffsetsPrint.Create(MyPrinter);
            bool result = await myPrint.SendPrnFileAsync(false);
        }

        private void SendImagePrint_Click(object sender, RoutedEventArgs e)
        {
            // There are two ways to send a print using the printer library.
            // One way is using the System.Drawing.PrintDocument class

            // Currently, the only way to set private devMode items such as Color Profiles, Saturation, and Quality, is through PrintDocument.
            // This will be shown here.
            PrintDocument doc = new PrintDocument()
            {
                PrintController = new StandardPrintController(),
                DocumentName = "Zion Print Test"
            };

            // Like normal, we set the printerSettings name and copies to specify the printer we wish to print to.
            doc.PrinterSettings.PrinterName = MyPrinter.PrinterName;
            try
            {
                doc.PrinterSettings.Copies = Int16.Parse(Copies.Text);
            }
            catch (Exception)
            {
                Copies.Text = "1";
                doc.PrinterSettings.Copies = 1;
            }

            // 0x100 is selected for custom paper size.
            doc.DefaultPageSettings.PaperSource.RawKind = 0x100;

            doc.DefaultPageSettings.PaperSize = GetPaperSizeForPrinter(MyPrinter);
            doc.DefaultPageSettings.Landscape = true;

            int quality;
            try
            {
                quality = Int32.Parse(PrintQuality.Text);    // 1-3 valid
            }
            catch (Exception)
            {
                quality = 3;            // Best
                PrintQuality.Text = "3";
            }

            int saturation;
            try
            {
                saturation = Int32.Parse(Intensity.Text);
            }
            catch (Exception)
            {
                saturation = 90;
                Intensity.Text = "90";
            }

            // Create a values object that will be used to set the private dev mode.
            var values = new PrivateDevModeValues()
            {
                Quality = (Quality)quality,
                Saturation = saturation,
                RotateImage180 = Rotate180.IsChecked.Value,
                MirrorImage = MirrorImage.IsChecked.Value
            };

            // Pass the settings in with the values. Primera.Print will handle the setting of the private devmode structure.
            MyPrinter.Print.SetPrivateSettings(doc.PrinterSettings, values);

            doc.PrintPage += Doc_PrintPage;

            doc.Print();
        }

        public PaperSize GetPaperSizeForPrinter(IPrimeraPrinter printer)
        {
            if (printer is IIP60Printer)
            {
                // Width for IP60 will always be 6 inches
                // By adding .08 inches to either side, we overbleed the image a little bit.
                // This helps the printer avoid unsightly white bars
                return new PaperSize("Custom", 608, 408) { RawKind = 0x100 };
            }
            else if (printer is IEddiePrinter)
            {
                // We will assume 3" cookies are loaded by default
                return new PaperSize("Custom", 300, 300) { RawKind = 0x100 };
            }
            else
            {
                // Otherwise we assume that a label printer is plugged in with a standard-size stock
                return new PaperSize("Custom", 400, 300) { RawKind = 0x100 };
            }
        }

        public static Stream GetPixelStream(BitmapSource source)
        {
            // This is a helper method to convert a bitmap source into a stream of pixels.
            // For use in printing directly from WPF into PrintDocument.

            MemoryStream outStream = new MemoryStream();
            BitmapEncoder enc = new BmpBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(source));
            enc.Save(outStream);

            return outStream;
        }

        private static Bitmap BitmapFromSource(BitmapSource bitmapsource)
        {
            // This is a helper method that converts a BitmapSource into a System.Drawing.Bitmap
            // For use in printing directly from WPF into PrintDocument.

            Bitmap bitmap = null;
            using (Stream outStream = GetPixelStream(bitmapsource))
            {
                if (outStream != null)
                {
                    bitmap = new Bitmap(outStream);
                }
            }

            return bitmap;
        }
        private void SendCutToPrinter(string printerName)
        {
            // Din 4x4-header från DTM
            byte[] header = CreateDynamicHeader(101.6, 101.6, true);

            // Klistra in din nya, perfekta HPGL-sträng här!
            string rawInkscape = "IN;PU2663,898;PD2661,904,2687,920,2711,938,2711,939,2734,958,2756,979,2755,979,2781,1007,2780,1007,2803,1036,2803,1037,2816,1056,2831,1080,2848,1106,2864,1132,2903,1197,2979,1325,3054,1455,3054,1456,3129,1586,3201,1716,3223,1757,3242,1794,3251,1812,3258,1828,3268,1852,3267,1852,3276,1876,3275,1877,3289,1927,3288,1927,3296,1978,3295,1978,3297,2022,3296,2022,3295,2052,3294,2052,3291,2082,3290,2082,3285,2112,3278,2141,3277,2141,3266,2176,3265,2176,3252,2211,3251,2210,3241,2232,3228,2257,3214,2284,3213,2284,3199,2311,3163,2377,3090,2507,3089,2507,3014,2638,2938,2768,2862,2895,2837,2934,2815,2969,2804,2986,2794,3001,2793,3000,2778,3021,2777,3020,2760,3040,2723,3077,2722,3076,2682,3108,2682,3107,2645,3131,2644,3130,2617,3145,2617,3144,2590,3156,2589,3156,2561,3166,2532,3175,2532,3174,2496,3182,2495,3181,2459,3187,2459,3186,2435,3188,2407,3189,2376,3190,2345,3191,2270,3193,2121,3194,1970,3194,1819,3194,1671,3191,1625,3189,1583,3188,1563,3186,1545,3185,1545,3184,1520,3181,1494,3176,1495,3175,1444,3162,1445,3161,1397,3142,1397,3141,1358,3121,1359,3120,1323,3098,1324,3097,1291,3071,1292,3071,1253,3034,1254,3033,1219,2991,1220,2991,1206,2971,1172,2918,1172,2917,1137,2860,1102,2802,1033,2684,967,2570,902,2457,854,2372,817,2304,795,2262,774,2221,758,2188,759,2188,747,2156,747,2155,737,2122,738,2122,730,2088,731,2088,725,2054,726,2054,723,2002,724,2002,725,1972,726,1972,729,1942,730,1942,735,1912,742,1883,743,1883,754,1847,755,1847,768,1813,769,1813,778,1793,779,1793,790,1770,791,1770,804,1745,818,1719,831,1693,867,1628,941,1498,942,1498,1037,1332,1037,1333,1133,1170,1165,1117,1196,1069,1210,1047,1222,1029,1223,1029,1234,1013,1235,1013,1262,980,1263,981,1293,951,1294,951,1327,924,1327,925,1351,908,1375,893,1376,894,1402,879,1403,880,1430,868,1458,858,1487,850,1523,842,1523,843,1559,837,1559,838,1583,836,1641,834,1702,832,1763,831,1888,830,2065,829,2065,830,2242,831,2324,832,2400,835,2433,836,2455,837,2455,838,2473,839,2473,840,2508,844,2508,845,2543,853,2578,863,2577,864,2611,876,2610,877,2637,889,2636,889,2662,903,2661,904,2687,920,2694,926;SP0;PU0,0;EX; ";

            // --- Tolken som fixar Inkscape-dialekten ---
            string hpgl = "";
            string[] commands = rawInkscape.Split(new char[] { ';' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (string cmd in commands)
            {
                if (cmd.StartsWith("PD"))
                {
                    string[] coords = cmd.Substring(2).Split(',');
                    for (int i = 0; i < coords.Length - 1; i += 2)
                    {
                        hpgl += $"PD{coords[i]},{coords[i + 1]};";
                    }
                }
                else
                {
                    hpgl += cmd + ";";
                }
            }
            // -------------------------------------------

            byte[] hpglBytes = System.Text.Encoding.ASCII.GetBytes(hpgl);
            byte[] fullJob = new byte[header.Length + hpglBytes.Length];

            Buffer.BlockCopy(header, 0, fullJob, 0, header.Length);
            Buffer.BlockCopy(hpglBytes, 0, fullJob, header.Length, hpglBytes.Length);

            RawPrinterHelper.SendBytesToPrinter(printerName, fullJob);
        }
        private void Doc_PrintPage(object sender, PrintPageEventArgs e)
        {
            // Our print callback.
            // We get the source of our image directly from the WPF image object "Zion".
            // It is returned as an ImageSource but we know that it will be a BitmapSource in this instance.

            RectangleF printArea = e.PageSettings.Bounds;

            e.Graphics.DrawImage(BitmapFromSource(Zion.Source as BitmapSource), 0, 0, printArea.Width, printArea.Height);
        }

        public byte[] CreateDynamicHeader(double widthMM, double heightMM, bool isPortrait)
        {
            double widthInches = widthMM / 25.4;
            double heightInches = heightMM / 25.4;

            ushort widthCount = (ushort)Math.Round(widthInches * 100.0);
            ushort heightCount = (ushort)Math.Round(heightInches * 100.0);

            if (!isPortrait)
            {
                ushort temp = widthCount;
                widthCount = heightCount;
                heightCount = temp;
            }

            byte[] header = new byte[16];
            header[0] = 0x1B; // ESC
            header[1] = 0x2A; // '*'
            header[2] = 0x12; // control ID
            header[3] = 0x0C; // header length
            header[4] = 0x40; // '@' signature

            // Height
            header[5] = (byte)((heightCount >> 8) & 0xFF);
            header[6] = (byte)(heightCount & 0xFF);
            // Width
            header[7] = (byte)((widthCount >> 8) & 0xFF);
            header[8] = (byte)(widthCount & 0xFF);

            for (int i = 9; i <= 14; i++) header[i] = 0x00;

            // Checksum calculator!
            int checksum = 0;
            for (int i = 0; i < 15; i++) checksum = (checksum + header[i]) & 0xFF;
            header[15] = (byte)checksum;

            return header;
        }

        #endregion Printing an Image
    }


}
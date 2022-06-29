using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace EpsgLibraryBuilder
{
  public partial class MainWindow : Window
  {
    CancellationTokenSource _cts = new CancellationTokenSource();

    public MainWindow()
    {
      InitializeComponent();
    }

    private async void EpsgLibraryBilderClickedAsync(object sender, RoutedEventArgs e)
    {
      Progress<ProgressReportModel> progress = new Progress<ProgressReportModel>();
      progress.ProgressChanged += ReportProgress;
      var outputPath = @"C:\data\EpsgDictionary\EpsgDictionary.csv";
      outputFileLabel.Content = outputPath;

      var watch = Stopwatch.StartNew();

      try
      {
        var epsgDictionary = await CreateWtkDictionaryAsync(progress, _cts.Token);
        progressBar.Value = 100;

        var crsNamesDictionary = CreateCrsToEpsgCodeDictionary(epsgDictionary);
        PrintCrsNamesToEpsgCodes(crsNamesDictionary);

        var codeToNameDictionary = DictionaryInverter(crsNamesDictionary);

        var csv = string.Join(Environment.NewLine, epsgDictionary.Select(d => $"{ d.Key };{ codeToNameDictionary[d.Key] };{ d.Value }"));
        System.IO.File.WriteAllText(outputPath, csv);

        WriteOnOutput($"{ Environment.NewLine } Total valid EPSG codes: { epsgDictionary.Count } { Environment.NewLine }");
      }
      catch (OperationCanceledException)
      {
        WriteOnOutput($"{ Environment.NewLine } { Environment.NewLine } .....................The download operation was cancelled... { Environment.NewLine } { Environment.NewLine }");
      }

      watch.Stop();


      WriteOnOutput($"{ Environment.NewLine } Total execution time: { watch.Elapsed }");
    }

    private void CancellClicked(object sender, RoutedEventArgs e)
    {
      _cts.Cancel();
    }

    private void ReportProgress(object sender, ProgressReportModel e)
    {
      progressBar.Value = e.PercentageComplete;

      PrintEmptyEpsgCodes(e.epsgInfoItems);
    }

    private Dictionary<string, int> CreateCrsToEpsgCodeDictionary(Dictionary<int, string> epsgDictionary)
    {
      var crsToEpsgCode = new Dictionary<string, int>();

      foreach (var item in epsgDictionary)
      {
        Match match = Regex.Match(item.Value, @"(?<="")(.*?)(?="")");
        if (match.Success)
          if (item.Key == 4978)
            crsToEpsgCode.Add("WGS 84 (DATUM)", item.Key); // Since 4326 is also called "WGS 84"
          else if (crsToEpsgCode.ContainsKey(match.Value))
            throw new InvalidOperationException("There is at least one duplicated EPSG name. Please review filtering out of repeated elements"); // all repeated CRS entry names should have been removed
          else
            crsToEpsgCode.Add(match.Value, item.Key);
      }

      return crsToEpsgCode;
    }

    private Dictionary<int, string> DictionaryInverter(Dictionary<string, int> nameToCodeDictionary)
    {
      var codeToNameDictionary = new Dictionary<int, string>();

      foreach (var item in nameToCodeDictionary)
        codeToNameDictionary.Add(item.Value, item.Key);

      return codeToNameDictionary;
    }

    private async Task<Dictionary<int, string>> CreateWtkDictionaryAsync(IProgress<ProgressReportModel> progress, CancellationToken cancellationToken)
    {
      var epsgToWKT = new ConcurrentDictionary<int, string>();
      var emptyEpsgCodes = new ConcurrentBag<EpsgWktInfo>();

      ProgressReportModel report = new ProgressReportModel();

      // EPSG codes vary from 1024 to 32767 (https://en.wikipedia.org/wiki/EPSG_Geodetic_Parameter_Dataset)
      var codes = Enumerable.Range(1024, 31743).ToList();
      //var codes = Enumerable.Range(1024, 10000).ToList();

      ParallelOptions po = new ParallelOptions();
      po.CancellationToken = cancellationToken;
      po.MaxDegreeOfParallelism = Environment.ProcessorCount;

      await Task.Run(() =>
      {
        Parallel.ForEach(codes, po, (code) =>
        {
          var url = "https://epsg.io/" + code + ".wkt";
          var epsgInfo = DownloadWkt(code, url);

          if (epsgInfo == null)
            emptyEpsgCodes.Add(new EpsgWktInfo(code, string.Empty));
          else if (epsgInfo.WTK.Length == 0)
            emptyEpsgCodes.Add(epsgInfo);
          else
            epsgToWKT.TryAdd(epsgInfo.EpsgCode, epsgInfo.WTK);

          cancellationToken.ThrowIfCancellationRequested();

          report.epsgInfoItems = emptyEpsgCodes;
          report.PercentageComplete = emptyEpsgCodes.Count * 100 / codes.Count;
          progress.Report(report);
        });
      });

      return new Dictionary<int, string>(epsgToWKT);
    }

    private EpsgWktInfo DownloadWkt(int epsgCode, string url)
    {
      try
      {
        using (var client = new WebClient())
        {
          var wkt = client.DownloadString(url);

          // In case a page is unavailable. This one, for example is unavailable while writing this code: https://epsg.io/6184.wkt
          if (wkt.Contains("Something went wrong."))
            return null;

          // In case the epsg wkt i unammed, since final cannot differentiate code by name. For example: https://epsg.io/7540.wkt
          if (wkt.Contains("unnamed"))
            return null;

          // In case the epsg CRS is a compounded one. For example: https://epsg.io/5834.wkt
          if (wkt.Contains("COMPD_CS"))
            return null;

          // In case the epsg CRS is a vertical one. For example: https://epsg.io/6180.wkt
          if (wkt.Contains("VERT_CS"))
            return null;

          // In case the Unknown dadum CRS. For example: https://epsg.io/4034.wkt
          if (wkt.Contains("Unknown") || wkt.Contains("unknown"))
            return null;

          // In case the deprecared CRS. For example: https://epsg.io/27584.wkt
          if (wkt.Contains("deprecated") || wkt.Contains("Unspecified") || wkt.Contains("example"))
            return null;

          // Removes Pulkovo, because too many entries with similar descriptions. For example: https://epsg.io/2500.wkt
          if (wkt.Contains("Pulkovo"))
            return null;

          // Removes NZGD, because too many entries for New Zealand. For example: https://epsg.io/27206.wkt
          if (wkt.Contains("NZGD"))
            return null;

          // Removes Beijing and similar, because too many entries with similar descriptions. For example: https://epsg.io/4774.wkt
          if (wkt.Contains("Beijing") || wkt.Contains("Xian") || wkt.Contains("Tokyo") || wkt.Contains("Oslo") || wkt.Contains("Paris") || wkt.Contains("Lisbon") || wkt.Contains("Santo") || wkt.Contains("Philippines") || wkt.Contains("Hawaii")
            || wkt.Contains("Sahara") || wkt.Contains("Balkans") || wkt.Contains("Congo") || wkt.Contains("Colombia") || wkt.Contains("Taiwan") || wkt.Contains("MOLDREF") || wkt.Contains("Serbian") || wkt.Contains("Canarias") || wkt.Contains("Garoua")
            || wkt.Contains("Miquelon") || wkt.Contains("Mayotte") || wkt.Contains("Cadastre") || wkt.Contains("China") || wkt.Contains("Antilles") || wkt.Contains("Mauritania") || wkt.Contains("Ouvea") || wkt.Contains("Slovenia") || wkt.Contains("Bermuda")
            || wkt.Contains("Croatian") || wkt.Contains("Libyan") || wkt.Contains("Caledonie") || wkt.Contains("Austria") || wkt.Contains("Deutsch") || wkt.Contains("Portugal") || wkt.Contains("Belge") || wkt.Contains("India") || wkt.Contains("Palestine")
            || wkt.Contains("British West Indies") || wkt.Contains("Madrid") || wkt.Contains("Adindan") || wkt.Contains("Namibia") || wkt.Contains("Cape") || wkt.Contains("Kalianpur") || wkt.Contains("Algerie") || wkt.Contains("Fiji") || wkt.Contains("Korea")
            || wkt.Contains("Faroe") || wkt.Contains("Panama") || wkt.Contains("Corrego") || wkt.Contains("Azores") || wkt.Contains("Hong Kong") || wkt.Contains("Chatham") || wkt.Contains("Jakarta") || wkt.Contains("Egypt") || wkt.Contains("Argentina")
            || wkt.Contains("Maracaibo") || wkt.Contains("Peru") || wkt.Contains("La Canoa") || wkt.Contains("Aratu") || wkt.Contains("Hungarian") || wkt.Contains("Tonga") || wkt.Contains("Israel") || wkt.Contains("Cayman") || wkt.Contains("Amersfoort")
            || wkt.Contains("Greenland") || wkt.Contains("Missouri") || wkt.Contains("California") || wkt.Contains("Comoros") || wkt.Contains("Washington") || wkt.Contains("Bangladesh") || wkt.Contains("Batavia") || wkt.Contains("Michigan") 
            || wkt.Contains("Sierra") || wkt.Contains("Swiss") || wkt.Contains("Antarctic") || wkt.Contains("Estonia") || wkt.Contains("Australia") || wkt.Contains("Mexican") || wkt.Contains("Campo") || wkt.Contains("Illinois") || wkt.Contains("Idaho")
            || wkt.Contains("Georgia") || wkt.Contains("Iowa") || wkt.Contains("Segara") || wkt.Contains("Sapper Hill") || wkt.Contains("Massachusetts") || wkt.Contains("Virginia") || wkt.Contains("Irish") || wkt.Contains("Utah") 
            || wkt.Contains("Wisconsin") || wkt.Contains("Wyoming") || wkt.Contains("Puerto") || wkt.Contains("Maine") || wkt.Contains("Kentucky") || wkt.Contains("Fatu") || wkt.Contains("Zanderij") || wkt.Contains("Hanoi") || wkt.Contains("Singapore")
            || wkt.Contains("Maupiti") || wkt.Contains("Ghana") || wkt.Contains("Timbalai") || wkt.Contains("Bern") || wkt.Contains("Quintana") || wkt.Contains("Tahiti") || wkt.Contains("Monte") || wkt.Contains("Naparima") || wkt.Contains("Pitcairn")
            || wkt.Contains("Reunion") || wkt.Contains("Maryland") || wkt.Contains("Massawa") || wkt.Contains("Grande") || wkt.Contains("Greek") || wkt.Contains("Tristan") || wkt.Contains("Solomon") || wkt.Contains("Sao Tome") || wkt.Contains("Principe")
            || wkt.Contains("Marigot") || wkt.Contains("Gan") || wkt.Contains("Diego") || wkt.Contains("Bogota") || wkt.Contains("Lanka") || wkt.Contains("Makassar") || wkt.Contains("Oregon") || wkt.Contains("Guadeloupe") || wkt.Contains("Minnesota")
            || wkt.Contains("Midway") || wkt.Contains("Delaware") || wkt.Contains("Helena") || wkt.Contains("Malongo") || wkt.Contains("Kandawala") || wkt.Contains("Padang") || wkt.Contains("Segora") || wkt.Contains("Rassadiran")
            || wkt.Contains("Herat") || wkt.Contains("Piscului") || wkt.Contains("Serindung") || wkt.Contains("Yacare") || wkt.Contains("Hito") || wkt.Contains("Accra") || wkt.Contains("Bukit") || wkt.Contains("Albania") || wkt.Contains("Piscului")
            || wkt.Contains("Madeira") || wkt.Contains("Luxembourg") || wkt.Contains("Alberta") || wkt.Contains("Malongo") || wkt.Contains("Colorado") || wkt.Contains("Alaska") || wkt.Contains("Combani") || wkt.Contains("Tahaa")
            || wkt.Contains("Mare") || wkt.Contains("Perroud") || wkt.Contains("Belep") || wkt.Contains("Lifou") || wkt.Contains("Florida") || wkt.Contains("Martinique") || wkt.Contains("Noumea") || wkt.Contains("Vientiane")
            || wkt.Contains("Gulshan") || wkt.Contains("Hiva") || wkt.Contains("Ghanem") || wkt.Contains("Yukon") || wkt.Contains("Kerguelen") || wkt.Contains("Jima") || wkt.Contains("Bellevue") || wkt.Contains("Area Astro") || wkt.Contains("Kusaie")
            || wkt.Contains("Rauenberg") || wkt.Contains("Vietnam") || wkt.Contains("Portuguese") || wkt.Contains("Chua") || wkt.Contains("Castillo") || wkt.Contains("Montana") || wkt.Contains("Nebraska") || wkt.Contains("Nevada")
            || wkt.Contains("New ") || wkt.Contains("Douala") || wkt.Contains("Ohio") || wkt.Contains("Ammassalik") || wkt.Contains("Oklahoma") || wkt.Contains("Ontario") || wkt.Contains("Dakota") || wkt.Contains("Vermont") || wkt.Contains("Gabon")
             || wkt.Contains("Lake") || wkt.Contains("Guyane") || wkt.Contains("Samboja") || wkt.Contains("Malal") || wkt.Contains("Hu Tzu") || wkt.Contains("Helle"))
            return null;

          // Removes NAD27, likely not used with modern seismic data. For example: https://epsg.io/26780.wkt
          if (wkt.Contains("NAD27"))
            return null;

          // Removes those from USA based on feet. For example: https://epsg.io/2230.wkt
          if (wkt.Contains("(ft"))
            return null;

          // Removes all SCAR and RGRDC, due to many and or duplicated named entries. For example: https://epsg.io/3204.wkt
          if (wkt.Contains(" SCAR ") || wkt.Contains("RGRDC") || wkt.Contains("SAD69") || wkt.Contains("RSRGD") || wkt.Contains("WGS 66") || wkt.Contains("WGS 72") || wkt.Contains("NSRS2007") || wkt.Contains("JAD") || wkt.Contains("DGN95") || wkt.Contains("GR96")
             || wkt.Contains("SIRGAS") || wkt.Contains("ITRF") || wkt.Contains("Hartebeesthoek") || wkt.Contains("SWEREF99") || wkt.Contains("HARN") || wkt.Contains("ETRS") || wkt.Contains("NGN") || wkt.Contains("DRUKREF") || wkt.Contains("MTM") || wkt.Contains("IGN72")
             || wkt.Contains("TUREF") || wkt.Contains("REGVEN") || wkt.Contains("Clarke") || wkt.Contains("Katastralni") || wkt.Contains("GDM2000") || wkt.Contains("PZ-90") || wkt.Contains("ISN") || wkt.Contains("JGD") || wkt.Contains("LKS92") || wkt.Contains("CSRS")
             || wkt.Contains("RGF") || wkt.Contains("IGM95") || wkt.Contains("Lao") || wkt.Contains("RGPF") || wkt.Contains("GDBD") || wkt.Contains("MARGEN") || wkt.Contains("RGR92") || wkt.Contains("CR05") || wkt.Contains("PNG94") || wkt.Contains("UCS") || wkt.Contains("FEH2010")
             || wkt.Contains("Qornoq") || wkt.Contains("ATS77") || wkt.Contains("AGD66") || wkt.Contains("KKJ") || wkt.Contains("PSAD56") || wkt.Contains("Afgooye") || wkt.Contains("Tananarive") || wkt.Contains("IRENET95") || wkt.Contains("Island") || wkt.Contains("Ferro")
             || wkt.Contains("RT38") || wkt.Contains("OSGB") || wkt.Contains("CH1903") || wkt.Contains("ST84") || wkt.Contains("MGI") || wkt.Contains("Kertau") || wkt.Contains("Moorea") || wkt.Contains("Pico") || wkt.Contains("Piscului") || wkt.Contains("Lake")
              || wkt.Contains("MOP") || wkt.Contains("SVY21") || wkt.Contains("Hjorsey 1955") || wkt.Contains(" 1952") || wkt.Contains(" 1949"))
            return null;

          // Removes all GAUSS, due to many entries. For example: https://epsg.io/2045.wkt
          if (wkt.Contains("Gauss"))
            return null;

          // Removes repeated entries, with same name but different versions. For example: https://epsg.io/3887.wkt
          if (epsgCode == 3887 || epsgCode == 4481)
            return null;


          return new EpsgWktInfo(epsgCode, wkt);
        }

      }
      catch (Exception)
      {
        return null;
      }
    }

    private void WriteOnOutput(string text)
    {
      output.Text += text;
    }

    private void PrintEmptyEpsgCodes(ConcurrentBag<EpsgWktInfo> epsgInfoItems)
    {
      output.Text = "Downloading all WTK (Well Known Texts) for EPSG codes between 1024 to 32767... \n\n";

      //foreach (var item in epsgInfoItems)
      //  output.Text += $" { item.EpsgCode } ; ";

      output.Text += $"{ Environment.NewLine } Total empty or invalid WKT strings: { epsgInfoItems.Count } { Environment.NewLine }";
    }

    private void PrintCrsNamesToEpsgCodes(Dictionary<string, int> crsToEpsgCode)
    {
      output.Text += "\n\n CRS names and EPGS codes... \n\n";

      foreach (var item in crsToEpsgCode)
        output.Text += $" { item.Key }  :  { item.Value } { Environment.NewLine }";

      output.Text += $"{ Environment.NewLine } Total CRS strings: { crsToEpsgCode.Count } { Environment.NewLine }";
    }
  }

  internal class EpsgWktInfo
  {
    internal int EpsgCode;
    internal string WTK;

    internal EpsgWktInfo(int code, string wtk)
    {
      EpsgCode = code;
      WTK = wtk;
    }
  }

  internal class ProgressReportModel
  {
    internal int PercentageComplete { get; set; } = 0;

    internal ConcurrentBag<EpsgWktInfo> epsgInfoItems { get; set; } = new ConcurrentBag<EpsgWktInfo>();

  }
}

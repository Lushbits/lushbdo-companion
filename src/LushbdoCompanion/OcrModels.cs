using System.Reflection;

namespace LushbdoCompanion;

/// <summary>
/// The PP-OCRv5 model files, carried inside the exe and unpacked once.
///
/// The release is one self-contained .exe and stays one: the four ONNX/dict
/// files ride as embedded resources rather than sitting beside it, because a
/// loose `models\` folder next to the download is exactly the "install
/// something" the app promises never to need. ONNX Runtime opens models by
/// path, so they are written to the app's own folder under %LOCALAPPDATA% on
/// first run and reused forever after — a cache the app owns, not an install.
///
/// Rewritten whenever the size on disk does not match the resource, which is
/// what makes a half-written file (a crash mid-unpack, a full disk) heal
/// itself on the next start instead of loading as a corrupt model.
/// </summary>
public static class OcrModels
{
    private static readonly string[] Files =
    [
        "ch_PP-OCRv5_mobile_det.onnx",
        "ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx",
        "latin_PP-OCRv5_rec_mobile_infer.onnx",
        "ppocrv5_latin_dict.txt",
    ];

    public static string Directory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "lushbdo-companion", "models", "ppocrv5");

    public static string Detector => Path.Combine(Directory, Files[0]);
    public static string Classifier => Path.Combine(Directory, Files[1]);
    public static string Recognizer => Path.Combine(Directory, Files[2]);
    public static string Dictionary => Path.Combine(Directory, Files[3]);

    /// <summary>Unpacks anything missing or the wrong size. Returns the folder.</summary>
    public static string Unpack()
    {
        System.IO.Directory.CreateDirectory(Directory);
        var assembly = Assembly.GetExecutingAssembly();
        foreach (var file in Files)
        {
            var name = "LushbdoCompanion.models." + file;
            using var source = assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException(
                    $"the OCR model {file} is missing from this build — rebuild from source.");
            var path = Path.Combine(Directory, file);
            if (File.Exists(path) && new FileInfo(path).Length == source.Length) continue;
            using var target = File.Create(path);
            source.CopyTo(target);
        }
        return Directory;
    }
}

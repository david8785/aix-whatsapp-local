using System.Text;

namespace AIXWhatsAppLocal;

/// <summary>
/// Manages the local folder structure for customer orders:
/// OrdersRoot\YYYY-MM-DD\HH\CustomerName_Phone_Count\image_0001.jpg
/// </summary>
public static class CustomerFolderService
{
    private static readonly char[] InvalidChars = Path.GetInvalidFileNameChars();

    /// <summary>
    /// Sanitize a name for use as a folder name (remove invalid filesystem chars).
    /// </summary>
    public static string SanitizeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Unknown";
        var sb = new StringBuilder();
        foreach (var c in name)
        {
            sb.Append(Array.IndexOf(InvalidChars, c) >= 0 ? '_' : c);
        }
        return sb.ToString().Trim();
    }

    /// <summary>
    /// Sanitize a phone number — keep digits only, minimum 7 digits.
    /// Returns "UnknownPhone" if not enough digits.
    /// </summary>
    public static string SanitizePhone(string phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return "UnknownPhone";
        var digits = new string(phone.Where(char.IsDigit).ToArray());
        return digits.Length >= 7 ? digits : "UnknownPhone";
    }

    /// <summary>
    /// Create a new order folder with count=1.
    /// Path: OrdersRoot\YYYY-MM-DD\HH\CustomerName_Phone_1
    /// </summary>
    public static string CreateOrderFolder(string ordersRoot, string customerName, string phone)
    {
        var now = DateTime.Now;
        var dateStr = now.ToString("yyyy-MM-dd");
        var hourStr = now.ToString("HH");
        var safeName = SanitizeName(customerName);
        var safePhone = SanitizePhone(phone);
        var folderName = $"{safeName}_{safePhone}_1";
        var folderPath = Path.Combine(ordersRoot, dateStr, hourStr, folderName);
        Directory.CreateDirectory(folderPath);
        return folderPath;
    }

    /// <summary>
    /// Get the order folder base path (without count suffix).
    /// Path: OrdersRoot\YYYY-MM-DD\HH\CustomerName_Phone
    /// </summary>
    public static string GetOrderFolderBase(string ordersRoot, string customerName, string phone)
    {
        var now = DateTime.Now;
        var dateStr = now.ToString("yyyy-MM-dd");
        var hourStr = now.ToString("HH");
        var safeName = SanitizeName(customerName);
        var safePhone = SanitizePhone(phone);
        return Path.Combine(ordersRoot, dateStr, hourStr, $"{safeName}_{safePhone}");
    }

    /// <summary>
    /// Find an existing customer folder by its base path (without count).
    /// Searches the parent directory for folders matching baseName_*.
    /// </summary>
    public static string? FindExistingFolder(string orderFolderBase)
    {
        var parentDir = Path.GetDirectoryName(orderFolderBase);
        var baseName = Path.GetFileName(orderFolderBase);
        if (parentDir == null || !Directory.Exists(parentDir)) return null;

        var found = Directory.GetDirectories(parentDir, baseName + "_*");
        return found.Length > 0 ? found[0] : null;
    }

    /// <summary>
    /// Save image bytes to the folder with a sequential name: image_0001.jpg
    /// Returns the full file path.
    /// </summary>
    public static string SaveImage(string folderPath, byte[] bytes, int index)
    {
        var fileName = $"image_{index:D4}.jpg";
        var filePath = Path.Combine(folderPath, fileName);
        File.WriteAllBytes(filePath, bytes);
        return filePath;
    }

    /// <summary>
    /// Rename the folder to match the actual file count.
    /// Example: דוד_0526908081_3 → דוד_0526908081_5 (after saving 2 more images).
    /// Returns the (possibly new) folder path.
    /// </summary>
    public static string UpdateFolderCount(string folderPath)
    {
        var dirName = Path.GetFileName(folderPath);
        var parentPath = Directory.GetParent(folderPath)?.FullName;
        if (parentPath == null) return folderPath;

        var lastUnderscore = dirName.LastIndexOf('_');
        if (lastUnderscore < 0) return folderPath;

        var baseName = dirName.Substring(0, lastUnderscore);
        var fileCount = CountFiles(folderPath);
        var newDirName = $"{baseName}_{fileCount}";
        var newPath = Path.Combine(parentPath, newDirName);

        if (folderPath == newPath || !Directory.Exists(folderPath)) return folderPath;

        if (Directory.Exists(newPath))
        {
            // Merge: move files from old to existing folder
            foreach (var file in Directory.GetFiles(folderPath))
            {
                var fileName = Path.GetFileName(file);
                var destFile = Path.Combine(newPath, fileName);
                if (!File.Exists(destFile))
                    File.Move(file, destFile);
            }
            Directory.Delete(folderPath, true);
            return newPath;
        }

        Directory.Move(folderPath, newPath);
        return newPath;
    }

    /// <summary>
    /// Count .jpg files in the folder.
    /// </summary>
    public static int CountFiles(string folderPath)
    {
        if (!Directory.Exists(folderPath)) return 0;
        return Directory.GetFiles(folderPath, "*.jpg").Length;
    }

    /// <summary>
    /// Get the next sequential image index (current count + 1).
    /// </summary>
    public static int GetNextImageIndex(string folderPath)
    {
        return CountFiles(folderPath) + 1;
    }
}
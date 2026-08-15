using System.Diagnostics;
using System.Text;

namespace AIXWhatsAppLocal;

/// <summary>
/// Manages the local folder structure for customer orders:
/// OrdersRoot\YYYY-MM-DD\HH-(HH+1)\CustomerName_Phone_Count\image_0001.jpg
///
/// Each daily folder also contains an "ALL" folder with directory junctions
/// to every customer folder created that day, so all orders are visible in one
/// place without duplicating image files.
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
    /// Compute the hourly bucket label: hour H → "HH-(HH+1)".
    /// Example: 9 → "09-10", 23 → "23-00".
    /// </summary>
    private static string HourBucket(DateTime now)
    {
        var h = now.Hour;
        var next = (h + 1) % 24;
        return $"{h:D2}-{next:D2}";
    }

    /// <summary>
    /// Create a new order folder with count=1.
    /// Path: OrdersRoot\YYYY-MM-DD\HH-(HH+1)\CustomerName_Phone_1
    /// Also creates a junction in the daily ALL folder pointing to it.
    /// </summary>
    public static string CreateOrderFolder(string ordersRoot, string customerName, string phone)
    {
        var now = DateTime.Now;
        var dateStr = now.ToString("yyyy-MM-dd");
        var hourStr = HourBucket(now);
        var safeName = SanitizeName(customerName);
        var safePhone = SanitizePhone(phone);
        var folderName = $"{safeName}_{safePhone}_1";
        var folderPath = Path.Combine(ordersRoot, dateStr, hourStr, folderName);
        Directory.CreateDirectory(folderPath);
        var dailyRoot = Path.Combine(ordersRoot, dateStr);
        EnsureAllJunction(dailyRoot, folderPath);
        return folderPath;
    }

    /// <summary>
    /// Get the order folder base path (without count suffix).
    /// Path: OrdersRoot\YYYY-MM-DD\HH-(HH+1)\CustomerName_Phone
    /// Note: uses DateTime.Now — prefer GetBasePathFromFolder for existing folders.
    /// </summary>
    public static string GetOrderFolderBase(string ordersRoot, string customerName, string phone)
    {
        var now = DateTime.Now;
        var dateStr = now.ToString("yyyy-MM-dd");
        var hourStr = HourBucket(now);
        var safeName = SanitizeName(customerName);
        var safePhone = SanitizePhone(phone);
        return Path.Combine(ordersRoot, dateStr, hourStr, $"{safeName}_{safePhone}");
    }

    /// <summary>
    /// Derive the base path (without count suffix) from an existing folder path.
    /// Avoids recomputing date/hour with a fresh DateTime.Now (hour-boundary safety).
    /// </summary>
    public static string GetBasePathFromFolder(string folderPath)
    {
        var dirName = Path.GetFileName(folderPath);
        var parent = Path.GetDirectoryName(folderPath);
        if (string.IsNullOrEmpty(parent)) return folderPath;
        var lastUnderscore = dirName.LastIndexOf('_');
        if (lastUnderscore < 0) return folderPath;
        var baseName = dirName.Substring(0, lastUnderscore);
        return Path.Combine(parent, baseName);
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
    /// Updates the ALL junction to point to the renamed folder.
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

        // Resolve daily root for ALL junction maintenance
        var hourFolder = Directory.GetParent(folderPath)?.FullName;
        var dailyRoot = hourFolder != null ? Directory.GetParent(hourFolder)?.FullName : null;

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
            RemoveAllJunction(dailyRoot, dirName);
            Directory.Delete(folderPath, true);
            EnsureAllJunction(dailyRoot, newPath);
            return newPath;
        }

        Directory.Move(folderPath, newPath);
        RemoveAllJunction(dailyRoot, dirName);
        EnsureAllJunction(dailyRoot, newPath);
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

    // ===== ALL junction management =====

    /// <summary>
    /// Create/update a directory junction in the daily ALL folder pointing to the
    /// customer folder. Junctions don't require admin privileges on Windows.
    /// </summary>
    private static void EnsureAllJunction(string? dailyRoot, string customerFolderPath)
    {
        if (string.IsNullOrEmpty(dailyRoot)) return;
        try
        {
            var allFolder = Path.Combine(dailyRoot, "ALL");
            Directory.CreateDirectory(allFolder);
            var linkName = Path.GetFileName(customerFolderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrEmpty(linkName)) return;
            var junctionPath = Path.Combine(allFolder, linkName);
            RemoveLinkIfExists(junctionPath);
            CreateJunction(junctionPath, customerFolderPath);
        }
        catch { /* best effort — ALL is a convenience view */ }
    }

    /// <summary>
    /// Remove a junction from the daily ALL folder if it exists.
    /// </summary>
    private static void RemoveAllJunction(string? dailyRoot, string customerFolderName)
    {
        if (string.IsNullOrEmpty(dailyRoot)) return;
        try
        {
            var junctionPath = Path.Combine(dailyRoot, "ALL", customerFolderName);
            RemoveLinkIfExists(junctionPath);
        }
        catch { }
    }

    /// <summary>
    /// Remove a filesystem link (junction/symlink) if it exists.
    /// Only removes reparse points — never deletes a real directory with contents.
    /// </summary>
    private static void RemoveLinkIfExists(string linkPath)
    {
        if (!Directory.Exists(linkPath)) return;
        var attrs = new DirectoryInfo(linkPath).Attributes;
        if ((attrs & FileAttributes.ReparsePoint) != 0)
        {
            Directory.Delete(linkPath); // removes the link, not the target
        }
    }

    /// <summary>
    /// Create a Windows directory junction (mklink /J) — no admin required.
    /// </summary>
    private static void CreateJunction(string junctionPath, string targetPath)
    {
        try
        {
            var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{junctionPath}\" \"{targetPath}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            var p = Process.Start(psi);
            p?.WaitForExit(5000);
        }
        catch { }
    }
}
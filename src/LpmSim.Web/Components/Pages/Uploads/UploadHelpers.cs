using ClosedXML.Excel;
using Microsoft.JSInterop;
using System.Globalization;

namespace LpmSim.Web.Components.Pages.Uploads;

/// <summary>
/// Shared Excel-cell parsing + template/download utilities used by every
/// upload component. Each upload deals only with its domain (header, rules,
/// commit) and lets these helpers handle ClosedXML quirks consistently.
/// </summary>
internal static class UploadHelpers
{
    public static string TryString(IXLCell cell)
    {
        try
        {
            if (cell is null) return "";
            var v = cell.Value;
            if (v.IsBlank || v.IsError) return "";
            if (v.IsText)     return v.GetText()?.Trim() ?? "";
            if (v.IsNumber)   return v.GetNumber().ToString(CultureInfo.InvariantCulture);
            if (v.IsDateTime) return v.GetDateTime().ToString("yyyy-MM-dd");
            if (v.IsBoolean)  return v.GetBoolean().ToString();
            return v.ToString()?.Trim() ?? "";
        }
        catch { return ""; }
    }

    public static (string value, string error) CellText(IXLRow row, int col, string name)
    {
        try
        {
            var cell = row.Cell(col);
            if (cell.Value.IsError) return ("", $"{name} (col {ColLetter(col)}) has formula error");
            return (TryString(cell), "");
        }
        catch (Exception ex) { return ("", $"{name} (col {ColLetter(col)}) unreadable: {ex.Message}"); }
    }

    public static (int? value, string error) CellInt(IXLRow row, int col, string name)
    {
        try
        {
            var cell = row.Cell(col);
            var v = cell.Value;
            if (v.IsBlank)  return (null, "");
            if (v.IsError)  return (null, $"{name} (col {ColLetter(col)}) has formula error");
            if (v.IsNumber) return ((int)v.GetNumber(), "");
            if (v.IsText)
            {
                var s = v.GetText()?.Trim() ?? "";
                if (string.IsNullOrEmpty(s)) return (null, "");
                if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                    return (parsed, "");
                return (null, $"{name} (col {ColLetter(col)}) = '{s}' not a number");
            }
            return (null, $"{name} (col {ColLetter(col)}) has unsupported type");
        }
        catch (Exception ex) { return (null, $"{name} (col {ColLetter(col)}) unreadable: {ex.Message}"); }
    }

    public static (decimal? value, string error) CellDecimal(IXLRow row, int col, string name)
    {
        try
        {
            var cell = row.Cell(col);
            var v = cell.Value;
            if (v.IsBlank)  return (null, "");
            if (v.IsError)  return (null, $"{name} (col {ColLetter(col)}) has formula error");
            if (v.IsNumber) return ((decimal)v.GetNumber(), "");
            if (v.IsText)
            {
                var s = (v.GetText()?.Trim() ?? "").TrimEnd('%');
                if (string.IsNullOrEmpty(s)) return (null, "");
                if (decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed))
                    return (parsed, "");
                return (null, $"{name} (col {ColLetter(col)}) = '{s}' not a number");
            }
            return (null, $"{name} (col {ColLetter(col)}) has unsupported type");
        }
        catch (Exception ex) { return (null, $"{name} (col {ColLetter(col)}) unreadable: {ex.Message}"); }
    }

    /// <summary>
    /// Parse a percent — accepts "25", "25%", and "0.25". Values whose absolute
    /// magnitude is &gt;= 1 are treated as percentages (divided by 100), &lt; 1
    /// as raw fractions. Returns the fraction (e.g. 0.25).
    /// <para>
    /// <paramref name="allowNegative"/> = true permits values down to -100% —
    /// used by Store Grades / Volume Groups where a negative markup or share
    /// is occasionally needed for special grade configurations.
    /// </para>
    /// </summary>
    public static (decimal? value, string error) CellPct(IXLRow row, int col, string name, bool allowNegative = false)
    {
        var (raw, err) = CellDecimal(row, col, name);
        if (err.Length > 0 || raw is null) return (raw, err);
        var v = raw.Value;
        if (!allowNegative && v < 0m) return (null, $"{name} = {v} is negative");
        if (allowNegative  && v < -100m) return (null, $"{name} = {v} below -100");
        if (v > 100m)                 return (null, $"{name} = {v} > 100");
        // Heuristic uses |v| so negatives mirror positives:
        //   25, -25 → 0.25, -0.25      (treated as percents)
        //   0.25, -0.25 → 0.25, -0.25  (treated as raw fractions)
        return (Math.Abs(v) >= 1m ? v / 100m : v, "");
    }

    public static (bool value, string error) CellBool(IXLRow row, int col, string name)
    {
        try
        {
            var cell = row.Cell(col);
            var v = cell.Value;
            if (v.IsBlank)   return (true, "");   // default = active
            if (v.IsError)   return (false, $"{name} (col {ColLetter(col)}) has formula error");
            if (v.IsBoolean) return (v.GetBoolean(), "");
            if (v.IsNumber)  return (v.GetNumber() != 0, "");
            if (v.IsText)
            {
                var s = (v.GetText()?.Trim() ?? "").ToLowerInvariant();
                return s switch
                {
                    "" or "y" or "yes" or "true"  or "1" or "active"   => (true, ""),
                    "n" or "no"  or "false" or "0" or "inactive"        => (false, ""),
                    _ => (false, $"{name} = '{s}' not boolean (yes/no/true/false)"),
                };
            }
            return (false, "");
        }
        catch (Exception ex) { return (false, $"{name} unreadable: {ex.Message}"); }
    }

    public static string ColLetter(int col)
    {
        if (col <= 26) return ((char)('A' + col - 1)).ToString();
        int a = (col - 1) / 26, b = (col - 1) % 26;
        return $"{(char)('A' + a - 1)}{(char)('A' + b)}";
    }

    /// <summary>Apply the standard dark-header styling used across all templates.</summary>
    public static void WriteHeader(IXLWorksheet sheet, string[] headers)
    {
        for (int i = 0; i < headers.Length; i++)
        {
            sheet.Cell(1, i + 1).Value = headers[i];
            sheet.Cell(1, i + 1).Style.Font.Bold = true;
            sheet.Cell(1, i + 1).Style.Fill.BackgroundColor = XLColor.FromHtml("#0f172a");
            sheet.Cell(1, i + 1).Style.Font.FontColor = XLColor.White;
        }
        sheet.Columns().AdjustToContents();
    }

    /// <summary>Save the workbook to a base-64 data URL and trigger a browser download.</summary>
    public static async Task DownloadAsync(IJSRuntime js, XLWorkbook wb, string fileName)
    {
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        var b64 = Convert.ToBase64String(ms.ToArray());
        var url = $"data:application/octet-stream;base64,{b64}";
        await js.InvokeVoidAsync("lpmDownload", url, fileName);
    }

    /// <summary>Copy an upload to a memory stream (capped at 20 MB).</summary>
    public static async Task<MemoryStream> ReadUploadAsync(Microsoft.AspNetCore.Components.Forms.IBrowserFile file)
    {
        var ms = new MemoryStream();
        await using (var src = file.OpenReadStream(20 * 1024 * 1024))
            await src.CopyToAsync(ms);
        ms.Position = 0;
        return ms;
    }

    /// <summary>True if the row has any non-empty cells (used to skip blank rows).</summary>
    public static bool RowIsUsed(IXLRow row)
    {
        try { return row.CellsUsed().Any(); } catch { return true; }
    }
}

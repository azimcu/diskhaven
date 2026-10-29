using DiskHaven.Core.Hideaway;

// Opens a DiskHaven hidden folder without the server: from a backup disk plugged into any PC.
//   diskhaven-recover <hideaway folder> <output folder>
// <hideaway folder> is ".diskhaven-data/hideaway" on a backup disk (or ".diskhaven/hideaway" in the data folder).
// The password or the recovery key is asked for; everything is written decrypted, as normal folders.

if (args.Length != 2)
{
    Console.WriteLine("""
        DiskHaven gizli klasör kurtarma / hidden folder recovery

          diskhaven-recover <kasa klasörü> <çıkış klasörü>

          <kasa klasörü>: yedek diskte .diskhaven-data/hideaway
          <çıkış klasörü>: şifresi çözülmüş dosyaların yazılacağı yer (normal klasörler olarak)

        Parola ya da kurtarma anahtarı sorulur.
        """);
    return 2;
}

var hideaway = Path.GetFullPath(args[0]);
var output = Path.GetFullPath(args[1]);
var users = Directory.Exists(hideaway) ? Directory.GetDirectories(hideaway).Where(d => File.Exists(Path.Combine(d, "slots.bin"))).ToList() : [];
if (users.Count == 0)
{
    Console.Error.WriteLine($"Kasa bulunamadı: {hideaway}");
    return 1;
}

Console.Write("Parola ya da kurtarma anahtarı: ");
var secret = ReadSecret();
Console.WriteLine();
Console.WriteLine("Açılıyor (birkaç saniye sürebilir)...");

// each subfolder is one user's vault; the secret opens at most one of them
foreach (var dir in users)
{
    var vault = HideawayStore.OpenFolder(dir, secret);
    if (vault is null) continue;
    var count = vault.ExportTo(output);
    Console.WriteLine($"Tamam: {count} dosya \"{output}\" klasörüne çıkarıldı.");
    return 0;
}
Console.Error.WriteLine("Parola ya da kurtarma anahtarı bu kasaların hiçbirini açmadı.");
return 1;

static string ReadSecret()
{
    if (Console.IsInputRedirected) return Console.ReadLine() ?? "";
    var s = new System.Text.StringBuilder();
    while (true)
    {
        var k = Console.ReadKey(intercept: true);
        if (k.Key == ConsoleKey.Enter) return s.ToString();
        if (k.Key == ConsoleKey.Backspace) { if (s.Length > 0) s.Length--; continue; }
        if (!char.IsControl(k.KeyChar)) s.Append(k.KeyChar);
    }
}

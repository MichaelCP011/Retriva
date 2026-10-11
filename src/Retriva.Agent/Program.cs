using Microsoft.Extensions.Logging;
using Retriva.Agent;
using Retriva.Shared;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHostedService<AgentWorker>();

// Membuat proses ini bisa dijalankan sebagai Windows Service maupun
// sebagai aplikasi konsol biasa saat pengembangan.
builder.Services.AddWindowsService(options => options.ServiceName = "RetrivaAgent");

/*
    LOG AGENT DITULIS KE BERKAS, dan sebelum ini tidak ditulis ke mana pun.

    Ini adalah kebalikan dari yang selama ini diasumsikan. Komentar di
    FileLogger.cs berbunyi "agent punya berkas log dan karena itu setiap kali ada
    masalah, yang tersedia untuk diperiksa hanyalah sisi agent" — dan itu keliru.
    Agent hanya punya log ketika dijalankan sebagai aplikasi konsol saat
    pengembangan. Dipasang sebagai layanan Windows, ia tidak punya konsol sama
    sekali, dan setiap baris yang ditulisnya mengalir ke tempat yang tidak ada.

    AddWindowsService memang menambahkan penyedia Event Log, tetapi tingkat
    minimumnya Warning. Seluruh diagnosis yang berguna — endpoint mana yang
    dicoba, rantai kegagalan TLS, sidik jari yang tidak cocok, penemuan ulang
    alamat lab — ditulis pada tingkat Information, jadi semuanya dibuang.

    Akibatnya persis yang terjadi berulang kali: unit yang tidak menyambung,
    tanpa satu pun jejak yang dapat dibaca siapa pun, dan diagnosis berjam-jam
    berdasarkan tebakan.

    Ditulis ke ProgramData\Retriva\logs — bukan ke Path.GetTempPath(), yang
    untuk layanan berjalan sebagai LocalSystem berarti C:\Windows\Temp.
*/
var agentLog = new FileLoggerProvider(RetrivaPaths.LogFile("agent"));
builder.Logging.AddProvider(agentLog);
builder.Logging.SetMinimumLevel(LogLevel.Information);

var host = builder.Build();

try
{
    await host.RunAsync();
}
finally
{
    // Dibuang secara eksplisit supaya baris terakhir benar-benar tertulis.
    // Layanan yang dihentikan Windows tidak selalu menjalankan finalizer, dan
    // baris terakhir sebelum berhenti justru yang paling sering dicari.
    agentLog.Dispose();
}

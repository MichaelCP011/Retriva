namespace Retriva.Server;

/// <summary>
/// Sidik jari sertifikat milik koordinator ini.
///
/// Dibungkus sebagai tipe tersendiri, bukan dioper sebagai string biasa lewat
/// wadah layanan. Sebuah string yang terdaftar sebagai layanan akan diambil
/// oleh apa pun yang kebetulan meminta string — dan yang berikutnya meminta
/// string adalah nama lab, kode pendaftaran, atau jalur folder data. Salah
/// satunya akan diam-diam menerima sidik jari sertifikat sebagai gantinya, dan
/// tidak ada yang gagal sampai seseorang membandingkan nilai yang keliru.
/// </summary>
public sealed record CoordinatorIdentity(string Thumbprint);

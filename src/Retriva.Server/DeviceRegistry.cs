// Berkas ini sengaja dikosongkan.
//
// DeviceRegistry berbasis devices.json digantikan Data/UnitStore.cs yang
// menyimpan token perangkat di SQLite. Alasannya: token adalah data, bukan
// konfigurasi, dan menyatukannya dengan tabel lain memberi transaksi serta
// constraint yang tidak dimiliki JSON.
//
// Data lama dipindahkan otomatis oleh UnitStore.MigrateLegacyJsonIfPresent().

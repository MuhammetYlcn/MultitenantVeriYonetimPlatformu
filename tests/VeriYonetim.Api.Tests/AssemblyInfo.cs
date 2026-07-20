// Tüm test sınıfları tek bir paylaşılan PostgreSQL test veritabanını (veriyonetim_test)
// kullanır ve her test başında TRUNCATE ile sıfırlar. xUnit varsayılanı farklı test
// SINIFLARINI paralel koşar; bu durumda bir sınıfın TRUNCATE'i diğerinin insert'lerinin
// ortasına girip yarış durumu (boş liste / 500) yaratır. Paylaşılan durum olduğundan
// paralelliği kapatıp testleri seri koşuyoruz.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]

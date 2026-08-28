// Kurulumun sunucu adresi. GELİŞTİRMEDE BİLEREK BOŞ.
//
// Bu dosya `window.API_BASE_URL` tanımlarsa panel o adresi kullanır; tanımlamazsa
// koddaki geliştirme varsayılanına (http://localhost:5000) düşer. Geliştirmede API
// zaten orada dinlediği için burada bir şey yazmaya gerek yok — dosyanın var olmasının
// sebebi, index.html'in onu istemesi ve olmayan dosyanın konsola 404 düşürmesi.
//
// TESLİMDE bu dosyanın üzerine, panel konteyneri açılışta kendi sürümünü yazar
// (bkz. docker/panel-entrypoint.sh): adres ortam değişkeninden gelir, verilmemişse
// panelin kendi adresi kullanılır. Yani adres imaja gömülü DEĞİL, kurulumun ayarı.
//
// Başka bir sunucuya elle bağlanmak için (ör. API'yi ağdaki başka bir makinede
// çalıştırmak) buraya şunu yazmak yeterli:
//   window.API_BASE_URL = "http://192.168.1.20:5000";

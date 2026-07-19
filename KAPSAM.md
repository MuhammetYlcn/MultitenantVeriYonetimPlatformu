# Kapsam Dokümanı — Yapay Zeka Destekli Multi-tenant Veri Yönetim ve İşlem Platformu

## Amaç
Birden fazla firmanın (tenant) aynı platform üzerinde birbirinden tamamen izole şekilde kendi veri setlerini yükleyip yönetebildiği, bu verileri doğal dilde sorgulayabildiği ve belge/fatura fotoğraflarından otomatik veri aktarımı yapabildiği bir platform geliştirmek. Veri kurum dışına çıkmadan, self-hosted bir yapay zeka ile anlamlandırılır (KVKK uyumlu kurumsal senaryolar için temel argüman).

## Kapsam İçi Özellikler
1. Kullanıcı/firma kaydı ve kimlik doğrulama (JWT tabanlı)
2. Tenant bazlı tam veri izolasyonu (şema düzeyinde)
3. CSV/Excel dosyalarından veri seti oluşturma (kolon ve veri tipi otomatik algılama)
4. Veri görüntüleme, filtreleme, agregasyon/dashboard
5. Doğal dilde sorgu: soru → SQL üretimi (Qwen2.5-Coder-7B) → sonuç tablosu ve grafiği
6. Belge/fatura fotoğrafından OCR ile veri aktarımı (Qwen2.5-VL)
7. Self-hosted model fine-tuning (QLoRA, Kaggle üzerinde) ve Ollama ile yerel çalıştırma

## Kapsam Dışı
- Gerçek zamanlı çoklu kullanıcı işbirliği (eş zamanlı düzenleme)
- Ödeme/faturalama sistemi
- Mobil push bildirimleri (Flutter uygulaması yalnızca temel CRUD + sorgu arayüzü sunar)
- Kural motoru (JSON-Logic tabanlı no-code kurallar) — ilk kapsamda yok, süre kalırsa değerlendirilir

## Teknoloji Yığını
- **Backend:** ASP.NET Core Web API (.NET 8) + Entity Framework Core
- **Veritabanı:** PostgreSQL — tenant'a özel şema, EF Core global query filter ile izolasyon
- **İstemci:** Flutter (web + mobil tek kod tabanı)
- **AI:** Qwen2.5-Coder-7B (NL→SQL), Qwen2.5-VL (OCR), QLoRA fine-tuning (Kaggle), GGUF + Ollama (yerel çalıştırma)
- **Altyapı:** Docker Compose (PostgreSQL; ileride Ollama/MailHog eklenecek)

## Teslimat Takvimi (haftalık demo hedefleri)
| Hafta | Kapsam | Demo |
|---|---|---|
| 1 (G5-9) | Auth + multi-tenant altyapı | İki tenant birbirinin verisine erişemiyor |
| 2 (G10-14) | CSV/Excel veri seti oluşturma | Tenant veri yükleyip filtreliyor |
| 3 (G15-19) | Analiz katmanı + Flutter arayüz | Platform çekirdeği uçtan uca çalışıyor |
| 4 (G20-24) | Doğal dilde sorgu (base model) | Soruya SQL üretilip sonuç+grafik gösteriliyor |
| 5 (G25-29) | Model eğitimi (QLoRA) | Fine-tuned model ile doğruluk artışı |
| 6 (G30-34) | OCR/fatura akışı | Fatura fotoğrafından otomatik veri girişi |
| 7 (G35-38) | Sertleştirme + teslim | Final sunum: izolasyon → veri → NL sorgu → OCR |

## Riskler (özet)
Model kalitesi/donanım yetersizliği durumunda daha küçük/quantize model ya da prompt-engineering'e ağırlık verilecek; zaman sarkması durumunda önce OCR fine-tuning, sonra Flutter'da özel tasarım feda edilecek — çekirdek (auth/multi-tenancy/veri/NL-SQL) hiçbir koşulda feda edilmez.



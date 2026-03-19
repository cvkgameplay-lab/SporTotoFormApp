# SporTotoFormApp Refactor Roadmap

Son guncelleme: 2026-03-19
Durum: Completed

## Hedef
15/15 ihtimalini korurken ayni zamanda ortak bilen kisi sayisi dusuk kuponlari secmek icin mevcut pipeline'i olasilik + deger optimizasyonuna cevirmek.

## Fazlar

- [x] Faz 1 - Mevcut akis analizi ve teknik borc temizligi
  - [x] Kullanilmayan Selenium bagimliliklarini servis akisindan ayikla
  - [x] Null/exception risklerini azalt
  - [x] API parse dayanikliligini guclendir

- [x] Faz 2 - Aday kupon uretimi ve on skorlama
  - [x] Uretim kisitlarini tek yerde topla (adet, ardillik, sembol dagilimi)
  - [x] Pozisyon bazli olasilik modeli ekle
  - [x] Gecmis sonuc dosyasi varsa otomatik ogrenme, yoksa guvenli varsayilan dagilim
  - [x] Ilk havuzu olasilik + yapi + cesitlilikle filtrele

- [x] Faz 3 - API sonrasi akilli secim
  - [x] API degerlendirme butcesi tanimla
  - [x] Asenkron/paralel API sorgu katmani ekle (sinirli eszamanlilik)
  - [x] Beklenen deger skoru (15/14/13 dagilim olasiligi + kisi sayisi cezasi)
  - [x] Son kupon setinde min Hamming mesafesi uygula

- [x] Faz 4 - Cikti, raporlama ve gozlenebilirlik
  - [x] Excel ciktilarina skor alanlari ekle
  - [x] TXT cikti ve mac-bazli dagilim raporunu genislet
  - [x] Loglarda havuz daralma metriklerini yayinla

- [x] Faz 5 - Dogrulama
  - [x] Derleme
  - [x] Temel calisma yolu kontrolu

## Uygulanan Ana Degisiklikler
1. Prediction uretimi iterator tabanli hale getirildi ve kurallar `PredictionGenerationRules` altinda toplandi.
2. `HistoricalOutcomeModel` eklendi. `Data/historical_results.txt` dosyasindan pozisyon-bazli olasiliklar otomatik ogreniliyor.
3. `CouponEvaluationService` eklendi:
   - On skorlama: log-likelihood + yapi cezasi
   - API sonrasi analiz: 15/14/13 olasilik dagilimi ve utility
4. `MoneyFilterService` bastan yazildi:
   - Top-K aday secimi (priority queue)
   - On havuz cesitlilik filtresi
   - API butce + sinirli paralellik
   - Utility bazli final secim + final cesitlilik
5. `SporTotoClient` retry/backoff, null-safe parse ve daha dayanikli HTML extraction ile guncellendi.
6. `ExcelExporter` kolonlari utility ve olasilik metrikleriyle genisletildi.
7. WinForms tarafinda kolon sayisi validasyonu ve daha guvenli calistirma akisı eklendi.
8. Kullanilmayan Selenium paketleri projeden kaldirildi.
9. `DataCekRequest` mantigi entegre edildi:
   - Resmi Spor Toto API'sinden gecmis hafta sonuclari cekiliyor
   - Sonuclar otomatik `Data/historical_results.txt` dosyasina yaziliyor
   - Cekim basarisiz olursa yerel dosya ile devam ediliyor (fallback)
10. Acik kaynak topluluklarda sik kullanilan "Monte Carlo portfoy optimizasyonu" yaklasimi eklendi:
   - Tek tek kupon yerine kupon seti optimize ediliyor
   - Simule edilen senaryolarda 15/14/13 kapsama degeri maksimize ediliyor
   - Final secimde cesitlilik korunuyor

## Operasyon Notu
- `SporTotoFormApp/Data/historical_results.txt` dosyasina en az 20 gecerli satir eklersen model daha iyi kalibre olur.
- Satir formati: yalnizca 15 karakter, semboller `1`, `X`, `2`.

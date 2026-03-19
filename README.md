# SporTotoFormApp

Spor Toto kuponlarini, olasilik ve beklenen deger (utility) odakli bir filtreleme akisiyla ureten WinForms uygulamasidir.

Bu proje "garanti kazanc" saglamaz. Ama hedeflenen sey sudur:
- 15/15 olasiligini makul tutmak
- Ayni kuponu bircok kisinin oynamasi riskini azaltmak
- Kupon seti genelinde cesitlilik ve kapsama arttirmak

## Ne Yapar?

Uygulama asagidaki pipeline ile calisir:

1. Gecmis sonuc verisini gunceller
- Resmi API: `https://webapi.sportoto.gov.tr/api/GameMatch/GetGameMatches/?gameRoundId=`
- Sonuclar `Data/historical_results.txt` dosyasina yazilir
- API erisilemezse yerel veri ile devam edilir

2. Aday kupon havuzu uretir
- 15 maclik 1/X/2 kombinasyonlari uretilir
- Ardarda ayni sonuc limiti uygulanir
- 1, X, 2 adet dagilimi kurallari uygulanir

3. On skorlama yapar
- Pozisyon bazli olasilik modeli (historical model)
- Yapisal ceza (asiri dengesiz dagilimlar icin)
- Top-K en iyi adaylar tutulur

4. Cesitlilik filtresi uygular
- Hamming distance ile benzer kuponlar elenir

5. API ile ikramiye/kisi sayisi kontrolu yapar
- Her aday kupon dis API'ye gonderilir
- 15 bilen kisi sayisi araligina gore filtrelenir

6. Utility hesabi yapar
- Kuponun P15, P14, P13 olasiliklari hesaplanir
- Kisi sayisina gore paylasim cezasi uygulanir
- Utility skoru uretilir

7. Monte Carlo portfoy secimi yapar
- Tek kupon degil kupon seti optimize edilir
- Binlerce senaryoda kapsama kazanci maksimize edilir
- Finalde min mesafe kurali korunur

8. Cikti dosyalarini olusturur
- `BestScoreCoupon.txt` -> sadece tahmin satirlari (entegrasyon icin)
- `Kuponlar.xlsx` -> detayli metrikler

## Kullanilan Ana Yontemler

### 1) Historical outcome model
`Services/HistoricalOutcomeModel.cs`
- `Data/historical_results.txt` satirlarini okur
- Her mac pozisyonu icin P(1), P(X), P(2) olasiligi cikarir
- Veri yetersizse default dagilim kullanir

### 2) Coupon evaluation (utility)
`Services/CouponEvaluationService.cs`
- Her kupon icin dogru bilme dagilimi hesaplanir
- P15, P14, P13 elde edilir
- Paylasim cezasi ile utility uretilir

### 3) Monte Carlo portfolio optimizer
`Services/MonteCarloPortfolioOptimizer.cs`
- Tarihsel modelden senaryo sonucu uretir
- Her kuponun senaryo puani hesaplanir
- Greedy secimle kupon setini secerek kapsama degerini arttirir

## Dosya Formati

### historical_results.txt
Konum: `SporTotoFormApp/Data/historical_results.txt`

Kurallar:
- Her satir tam 15 karakter olmali
- Sadece `1`, `X`, `2` karakterleri olmali

Ornek:
```txt
1X211X12X121X2
XX121211X21X112
```

### BestScoreCoupon.txt
Konum: uygulama output klasoru

Her satirda sadece bir kupon tahmini vardir:
```txt
1X2X21112X11121
2X21XX1211X2121
...
```

Not: Bu dosya dis otomasyon programina besleme icin sade tutulmustur.

## Proje Yapisi (Ozet)

- `Form1.cs` -> UI ve calistirma tetigi
- `Services/MoneyFilterService.cs` -> ana orchestration pipeline
- `Services/PredictionListHelper.cs` -> aday kupon uretimi
- `Services/HistoricalResultsUpdateService.cs` -> resmi API'den gecmis veri cekme
- `Services/HistoricalOutcomeModel.cs` -> pozisyon bazli olasilik modeli
- `Services/CouponEvaluationService.cs` -> utility/P15/P14/P13
- `Services/MonteCarloPortfolioOptimizer.cs` -> final kupon seti optimizasyonu
- `Client/SporTotoClient.cs` -> dis kupon API istemcisi
- `Services/ExcelExporter.cs` -> Excel rapor cikisi

## Kurulum ve Calistirma

Gereksinimler:
- .NET 8 SDK
- Windows (WinForms)

Adimlar:
1. Cozumu ac: `SporTotoFormApp.sln`
2. Build al:
```bash
dotnet build SporTotoFormApp.sln
```
3. Uygulamayi calistir
4. UI'dan kolon sayisini gir
5. `CALISTIR` tusuna bas

## Parametre ve Performans Notlari

`Services/OptimizationOptions.cs` uzerinden degistirilebilir:
- `InitialTopCandidateLimit`
- `DiversePrePoolLimit`
- `ApiBudgetMultiplier`
- `ApiConcurrency`
- `MinHammingDistance`
- `MinHammingDistanceFinal`
- `MonteCarloScenarioCount`

Senaryo sayisi artarsa kalite artabilir, ama sure de artar.

## Onemli Uyari

Bu sistem istatistiksel bir optimizasyon aracidir. Finansal sonuc garantisi vermez.
Spor Toto gibi oyunlarda belirsizlik yuksektir; ciktilar karar destek amaclidir.

## Yol Haritasi

Refactor ve gelisim durumu:
- `SporTotoFormApp_Refactor_Roadmap.md`

# SporTotoFormApp

SporTotoFormApp, Spor Toto icin 15 maclik 1/X/2 kupon setleri ureten, resmi mac/sonuc verisi ve yerel SQL Server gecmisiyle tahminleri zenginlestiren bir Windows Forms uygulamasidir.

Uygulama tek bir "en iyi kupon" bulmaya calismaktan cok, belirlenen kolon sayisi kadar cesitli, olasilik olarak dengeli ve ikramiye paylasim riski goreli olarak dusuk bir portfoy olusturmayi hedefler. Mac listesini resmi Spor Toto API'sinden alir, gecmis sonuclari SQL Server'a yazar, Nesine program ve oynanma verilerini modele dahil eder, aday kuponlari harici ikramiye simulatorden gecirir ve final kupon setini Monte Carlo tabanli portfoy secimiyle daraltir.

> Onemli: Bu proje kazanc garantisi vermez. Spor Toto sonuc belirsizligi yuksektir; uygulama istatistiksel karar destek ve otomasyon aracidir.

## Ana Islevler

- Guncel tahmin haftasini resmi Spor Toto API'sinden bulur.
- Yalnizca 15 macli ve sonucu henuz tamamlanmamis tahmin programlarini secmeye calisir.
- Eski ve yeni Spor Toto round araliklarini tarayarak sonuclanmis haftalari SQL Server'a aktarir.
- 15 maclik sonuc satirini, mac detaylarini, takim bilgilerini, skorlarini ve ikramiye bilgilerini saklar.
- Gecmis sonuc dagilimindan pozisyon bazli P(1), P(X), P(2) olasiliklari uretir.
- Takim gecmisi, ic saha/dis saha performansi ve ayni eslesme gecmisiyle mac bazli olasiliklari gunceller.
- Nesine programini, oynanma oranlarini, H2H ozetlerini ve ek endpoint verilerini snapshot olarak DB'ye yazar.
- Nesine kaynakli populerlik ve oran sinyallerini DB tahmin modeline ekler.
- 15 maclik aday kuponlari uretir ve temel yapisal filtrelerden gecirir.
- Adaylari on skor, Hamming mesafesi, harici ikramiye cevabi ve utility hesabiyla azaltir.
- Monte Carlo senaryolariyla final kupon portfoyunu secer.
- Uretilen kuponlari Excel, txt ve SQL Server'a kaydeder.
- Sonucu gelmis eski tahmin run'larini gercek sonuc satiriyla karsilastirir.

## Kullanici Arayuzu

Uygulama acildiginda ilk ekranda "Tahmin Edilecek Hafta" bolumu guncel programi yukler. Bu alanda round adi, RoundId, mac sayisi ve mac listesi gorunur. Mac listesinde her satir icin:

- Mac sirasi
- Ev sahibi - deplasman
- Mac tarihi
- Modelin P1 / PX / P2 olasiliklari
- Final kupon setinde o mac icin kac adet 1 / X / 2 secildigi
- Lig veya tur bilgisi

sutunlari yer alir.

Sag taraftaki "DB Model" sekmesinde kolon sayisi ve optimizasyon parametreleri bulunur. Buradaki degerler run bazinda kullanilir ve run metadata'si olarak veritabanina yazilir.

Ustteki ana butonlar:

- `CALISTIR`: Tahmin pipeline'ini baslatir, kuponlari uretir, dosyalari ve DB kayitlarini olusturur.
- `SONUC DEGERLENDIR`: Once resmi API'den gecmis sonuclari gunceller, sonra sonucu gelmis tahmin run'larini gercek sonuc satiriyla karsilastirir.
- `Tahmin Dosyasini Ac`: Uygulama output klasorundeki sade tahmin dosyasini acar.

Alt log paneli pipeline'in hangi adimda oldugunu, kac aday uretildigini, DB snapshot durumunu, run kaydini ve degerlendirme sonucunu zaman damgalariyla gosterir.

## Calisma Akisi

### 1. Tahmin haftasinin yuklenmesi

`HistoricalResultsUpdateService.GetLatestRoundForPredictionAsync` resmi Spor Toto `GameMatch` endpoint'ini tarar. Modern round araliginda dolu programlari bulur, 15 macli olmayan programlari eler ve sonucu tamamlanmis round'lari tahmin haftasi olarak secmez.

Secim mantigi kisaca:

- API'den mac listesi alinir.
- Ev sahibi ve deplasman bilgisi olmayan satirlar kullanilmaz.
- Mac sayisi 15 olmayan program tahmin haftasi disinda birakilir.
- Tum maclarin `fullTimeWin` veya `noterWin` sonucu varsa program tamamlanmis kabul edilir.
- Aktif veya en yakin oynanacak 15 macli program secilir.

Bu sayede ileri tarihli ama farkli formatli programlarin veya eksik macli round'larin tahmin ekrani olarak secilmesi engellenir.

### 2. Gecmis sonuc verisinin guncellenmesi

`HistoricalResultsUpdateService.RefreshAsync` hem modern hem legacy round araliklarini tarar. Modern round'lar once indirildigi icin yeni sonuclanmis haftalar DB'ye daha hizli yansir.

Indirilen veriler su kaynaklardan gelir:

- Mac listesi: `https://webapi.sportoto.gov.tr/api/GameMatch/GetGameMatches/?gameRoundId=`
- Ikramiye sonucu: `https://webapi.sportoto.gov.tr/api/GameResult/GetGameResultByGameRoundId?id=`

Sonuclanmis 15 macli programlar `HistoricalResults`, `HistoricalResultMatches`, `HistoricalResultPayouts` ve `Teams` tablolarina yazilir.

API erisimi veya veri formati sorunlarinda uygulama tamamen durmak yerine mevcut SQL Server verisi veya yerel seed dosyasi ile devam etmeye calisir.

### 3. Model sinyallerinin hazirlanmasi

Tahmin haftasi yuklendikten sonra uygulama cesitli veri kaynaklarini birlestirir:

- Gecmis Spor Toto sonuc satirlari
- Gecmis mac/takim performansi
- Ev sahibi takim ic saha gecmisi
- Deplasman takimi dis saha gecmisi
- Ayni eslesme gecmisi
- Nesine program verisi
- Nesine oynanma oranlari
- Nesine H2H ve ek endpoint snapshot'lari
- H2H verilerinden uretilen feature tablosu
- Gecmis ikramiye ve 15 bilen kisi sayisi dagilimi

`PredictionInsightRepository` bu kaynaklari okuyarak mac bazinda P1/PX/P2 olasiliklarini olusturur. Modelde veri azsa daha muhafazakar veya varsayilan dagilimlara geri donulur.

### 4. Aday kupon uretimi

`PredictionListHelper` 15 mac icin `1`, `X`, `2` kombinasyonlarini uretir. Tum 3^15 uzayi dogrudan kullanilmaz; ilk uretim asamasinda temel dagilim kurallari uygulanir:

- Mac sayisi: 15
- Gecerli semboller: `1`, `X`, `2`
- Arka arkaya ayni sembol limiti: 3
- 1 sayisi: 5-9
- X sayisi: 2-6
- 2 sayisi: 2-6

Bu kurallar asiri dengesiz veya yapisal olarak zayif adaylari pipeline'in basinda azaltir.

### 5. On skorlama

`HistoricalOutcomeModel` ve `CouponEvaluationService` aday kuponlari on skorlamadan gecirir. Her kupon icin:

- Pozisyon bazli sembol olasiliklari okunur.
- Kuponun modelle uyumu hesaplanir.
- Asiri tekduze veya riskli dagilimlara ceza uygulanir.
- En yuksek skorlu Top-K adaylar tutulur.

Top-K limitleri UI'daki `InitialTopCandidateLimit` parametresiyle kontrol edilir.

### 6. Cesitlilik filtresi

Pipeline, cok benzer kuponlardan olusan bir liste yerine kapsama alani genis bir kupon havuzu olusturmak ister. Bu nedenle adaylar Hamming distance filtresinden gecer.

`MinHammingDistance`, on havuzdaki kuponlar arasindaki minimum farki belirler. Final secimde ise `MinHammingDistanceFinal` kullanilir.

### 7. Harici ikramiye/kisi sayisi kontrolu

`SporTotoClient`, aday kuponlari `sporzip.com/spor-toto-ne-verir` servisine multipart form olarak gonderir ve HTML cevabindan 15, 14, 13, 12 bilen kisi sayisi ile ikramiye metnini parse eder.

Bu adimda:

- Aday kuponlar API butcesine gore sinirlanir.
- API eszamanliligi `ApiConcurrency` ile kontrol edilir.
- Parse edilemeyen veya bos cevaplar elenir.
- 15 bilen kisi sayisi `MinI15WinnerCount` ve `MaxI15WinnerCount` araligina gore filtrelenir.

DB ikramiye gecmisi yeterliyse uygulama i15 hedef araligini gecmis 15 bilen kisi sayisi dagilimina gore revize edebilir.

### 8. Utility hesabi

`CouponEvaluationService` her kupon icin P15, P14 ve P13 olasiliklarini hesaplar. Sonra harici servisten gelen kisi sayilarini kullanarak paylasim riskini dikkate alan bir utility skoru uretir.

Utility hesabi kabaca su hedefleri dengeler:

- 15 bilme olasiligi
- 14 ve 13 bilme olasiliklari
- Ikramiyenin cok kisiyle paylasilma riski
- Modelin genel olasilik dagilimiyle uyum

Final siralama tek metrikle yapilsa da uygulama Excel ciktisinda P15, P14, P13 ve kisi sayilarini ayri ayri saklar.

### 9. Monte Carlo portfoy optimizasyonu

`MonteCarloPortfolioOptimizer`, final kupon setini tek tek en yuksek utility'li kuponlari almak yerine portfoy olarak optimize eder.

Calisma mantigi:

- Model olasiliklarindan binlerce sanal 15 mac sonucu uretilir.
- Her aday kuponun bu senaryolardaki basarisi hesaplanir.
- Greedy secimle portfoye en cok ek kapsama degeri katan kupon secilir.
- Ayni maca ayni sembolun asiri yigilmasi ve benzer kupon secimi sinirlanir.
- Finalde hedef kolon sayisina ulasilmaya calisilir.

`MonteCarloScenarioCount` arttikca senaryo temsili guclenir, ancak calisma suresi de artar.

### 10. Cikti ve run kaydi

Pipeline tamamlandiginda uygulama:

- `BestScoreCoupon.txt` dosyasina sadece 15 karakterlik tahmin satirlarini yazar.
- `Kuponlar.xlsx` dosyasina tahmin, 15/14/13/12 bilen kisi sayilari, utility, P15, P14, P13 kolonlarini yazar.
- `PredictionRuns` tablosuna run ozetini kaydeder.
- `Predictions` tablosuna her kuponu kaydeder.
- `PredictionRunModelInfo` tablosuna o run'da kullanilan RoundId, RoundName, Nesine program no ve optimizasyon ayarlarini kaydeder.
- `PredictionRunMatchMatrix` tablosuna mac bazinda final kupon dagilimini kaydeder.

Bu bilgiler sonraki "SONUC DEGERLENDIR" islemi icin kullanilir.

## Sonuc Degerlendirme

`SONUC DEGERLENDIR` butonu eski tahmin run'larini sonucuyla karsilastirir.

Islem sirasinda:

1. Resmi Spor Toto API'sinden gecmis sonuclar tekrar guncellenir.
2. DB'de henuz degerlendirilmemis run'lar bulunur.
3. Run icin dogru sonuc satiri cozulur.
4. Her kuponun gercek sonuca gore kac mac bildigi hesaplanir.
5. Run ozet sonucu `PredictionRunResults` tablosuna yazilir.

Sonuc eslestirme onceligi:

- Run sirasinda kaydedilen mac matrisi ile `HistoricalResultMatches` eslesmesi
- `PredictionRunModelInfo.RoundId`
- `PredictionRunModelInfo.RoundName`

Eski run'larda RoundId, RoundName veya mac matrisi yoksa uygulama yanlis haftaya baglamamak icin otomatik degerlendirme yapmaz.

## SQL Server Veritabani

Baglanti `Data/Database.cs` icinde tanimlidir:

```csharp
Server=DESKTOP-27OP6L7;Database=SporTotoFormApp;Integrated Security=True;Encrypt=False;TrustServerCertificate=True;
```

Uygulama bazi tablolari ve kolonlari ihtiyac halinde kendisi olusturur veya eksik kolonlari ekler.

Ana tablolar:

- `HistoricalResults`: RoundId, sonuc satiri, sezon, hafta ve round adi.
- `HistoricalResultMatches`: Gecmis haftalardaki 15 macin takim, tarih, skor ve sonuc sembolu bilgisi.
- `HistoricalResultPayouts`: 15/14/13/12 bilen kisi sayisi ve ikramiye bilgileri.
- `Teams`: API takim kimlikleri ve takim adlari.
- `NesinePopularitySnapshots`: Nesine program ve oynanma orani snapshot'lari.
- `NesineHeadToHeadSnapshots`: Nesine H2H ozet snapshot'lari.
- `NesineHeadToHeadExtraSnapshots`: Ek Nesine endpoint cevaplari.
- `MatchModelFeatures`: H2H ve oran verilerinden turetilen mac feature degerleri.
- `PredictionRuns`: Tahmin run ozeti.
- `Predictions`: Run icindeki tekil kuponlar ve metrikleri.
- `PredictionRunModelInfo`: Run sirasinda kullanilan model ve optimizasyon ayarlari.
- `PredictionRunMatchMatrix`: Final kupon setinin mac bazli 1/X/2 dagilimi.
- `PredictionRunResults`: Run'in gercek sonuca gore degerlendirme ozeti.

## Dosya Ciktilari

### BestScoreCoupon.txt

Konum: uygulama output klasoru.

Her satir 15 karakterlik sade kupon tahminidir. Harici otomasyon veya manuel oynama icin kolay kopyalanabilir.

```txt
1X2X21112X11121
2X21XX1211X2121
```

### Kuponlar.xlsx

Konum: uygulama output klasoru.

Excel raporu su alanlari icerir:

- Tahmin
- 15 bilen kisi
- 14 bilen kisi
- 13 bilen kisi
- 12 bilen kisi
- Utility
- P15
- P14
- P13

### historical_results.txt

Konum: `SporTotoFormApp/Data/historical_results.txt`

SQL Server bos ise baslangic seed verisi olarak kullanilabilir. Her satir tam 15 karakter olmali ve sadece `1`, `X`, `2` icermelidir.

## Optimizasyon Parametreleri

UI'daki DB Model paneli ve `OptimizationOptions` sinifi su ayarlari kullanir:

- `Kolon Sayisi`: Finalde istenen kupon adedi.
- `i15 Min`: Harici servisten gelen 15 bilen kisi sayisi alt limiti.
- `i15 Max`: Harici servisten gelen 15 bilen kisi sayisi ust limiti.
- `InitialTopCandidateLimit`: On skorlamadan sonra tutulacak maksimum aday sayisi.
- `DiversePrePoolLimit`: Cesitlilik filtresinden sonra kalacak aday havuzu.
- `ApiBudgetMultiplier`: Hedef kolon sayisina gore harici API'ye kac aday gonderilecegini belirleyen carpan.
- `ApiConcurrency`: Harici API isteklerinde eszamanlilik limiti.
- `MinHammingDistance`: On havuzda kuponlar arasindaki minimum fark.
- `MinHammingDistanceFinal`: Final kupon setinde minimum fark.
- `MonteCarloScenarioCount`: Portfoy seciminde uretilen sanal sonuc senaryosu sayisi.

Parametreleri buyutmek kaliteyi artirabilir, ancak sureyi, bellek kullanimini ve harici API yukunu de artirir.

## Proje Yapisi

- `Program.cs`: WinForms uygulama giris noktasi.
- `Form1.cs`: UI, kullanici etkilelesimi, run baslatma, sonuc degerlendirme ve ekrandaki mac matrisi.
- `Interfaces/ITestView.cs`: Servislerin UI log/progress ile haberlesmesi icin ortak arayuz.
- `Object/Bonus.cs`: Kupon ve ikramiye DTO siniflari.
- `Client/SporTotoClient.cs`: Harici ikramiye/kisi sayisi servisine istek atan istemci.
- `Services/PredictionListHelper.cs`: 15 maclik aday kupon uretimi ve temel dagilim kurallari.
- `Services/OptimizationOptions.cs`: UI ve pipeline optimizasyon ayarlari.
- `Services/MoneyFilterService.cs`: Ana tahmin pipeline orchestration servisi.
- `Services/HistoricalResultsUpdateService.cs`: Resmi Spor Toto API'sinden mac, sonuc ve ikramiye verisi cekme.
- `Services/HistoricalOutcomeModel.cs`: Gecmis sonuc satirlarindan pozisyon bazli olasilik modeli.
- `Services/CouponEvaluationService.cs`: P15/P14/P13 ve utility hesabi.
- `Services/MonteCarloPortfolioOptimizer.cs`: Final kupon seti optimizasyonu.
- `Services/NesineProgramService.cs`: Nesine program verisi cekme.
- `Services/NesineHeadToHeadService.cs`: Nesine H2H ve ek endpoint verisi cekme.
- `Services/ExcelExporter.cs`: Excel raporu olusturma.
- `Data/Database.cs`: SQL Server baglantisi.
- `Data/HistoricalResultRepository.cs`: Gecmis sonuc, mac, takim ve ikramiye tablolarini yonetme.
- `Data/PredictionInsightRepository.cs`: DB kaynakli mac olasilik modeli ve ikramiye insight uretimi.
- `Data/NesineProgramRepository.cs`: Nesine program snapshot kaydi.
- `Data/NesineHeadToHeadRepository.cs`: Nesine H2H snapshot kaydi.
- `Data/MatchModelFeatureRepository.cs`: H2H ve ek verilerden model feature tablosu uretimi.
- `Data/PredictionRepository.cs`: Tahmin run kaydi, kupon kaydi ve sonuc degerlendirme.

## Kurulum

Gereksinimler:

- Windows
- .NET 8 SDK
- SQL Server erisimi
- Internet erisimi

Projeyi derlemek icin:

```bash
dotnet build SporTotoFormApp.sln
```

Visual Studio ile calistirmak icin:

1. `SporTotoFormApp.sln` dosyasini ac.
2. SQL Server baglanti bilgisini gerekirse `Data/Database.cs` icinde duzenle.
3. Projeyi `Debug` veya `Release` modunda derle.
4. Uygulamayi baslat.
5. Tahmin haftasinin dogru yuklendigini kontrol et.
6. Kolon sayisi ve optimizasyon ayarlarini belirle.
7. `CALISTIR` ile kuponlari uret.
8. Sonuclar geldikten sonra `SONUC DEGERLENDIR` ile run performansini karsilastir.

## Dis Servisler

Uygulama birden fazla dis kaynaga baglidir:

- Resmi Spor Toto mac API'si
- Resmi Spor Toto sonuc/ikramiye API'si
- Nesine program ve H2H endpoint'leri
- Sporzip ikramiye/kisi sayisi simulatoru

Bu servislerdeki kesinti, HTML/JSON format degisikligi veya oran/veri gecikmesi pipeline sonucunu etkileyebilir. Kod bircok noktada hatayi loglayip mevcut DB verisiyle devam etmeye calisir.

## Bilinen Sinirlar

- Uygulama yalnizca 15 maclik Spor Toto formatini hedefler.
- Harici HTML parse edilen servislerde sayfa yapisi degisirse parser guncellenmelidir.
- Eski tahmin run'larinda RoundId, RoundName veya mac matrisi yoksa sonuc degerlendirme otomatik yapilmaz.
- SQL Server baglanti stringi su an kaynak kodda sabittir.
- `bin` ve `obj` klasorleri derleme ciktisidir; kaynak kod degisikligi olarak takip edilmemelidir.
- Uygulama tahmin kalitesini arttirmaya calisir, ancak spor karsilasmalarinda kesinlik yoktur.

## Yol Haritasi

Ek refactor ve gelisim notlari:

- `SporTotoFormApp_Refactor_Roadmap.md`

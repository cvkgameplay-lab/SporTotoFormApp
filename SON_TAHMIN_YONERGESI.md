# Son Tahmin Uretim Yonetimi

Bu hafta tahmin uretirken hedef, sadece 15/15 kovalamak degil; gecmise asiri uyan parametreleri elemek, portfoy dagilimini kontrol etmek ve maliyet disiplinini korumaktir.

## Uretim Sirasi

1. Visual Studio'da `SporTotoFormApp.sln` dosyasini ac.
2. Uygulamayi `Debug` modunda baslat.
3. Sol ustte tahmin haftasinin dogru yuklendigini kontrol et:
   - RoundId dolu olmali.
   - Mac sayisi 15 olmali.
   - Maclar oynanmamis/guncel hafta olmali.
4. `SONUC DEGERLENDIR` butonuna bir kez bas.
   - Gecmis sonuclar guncellensin.
   - Eski run'larin sonucu DB'ye islensin.
5. `PARAMETRE OTOPSISI` butonunda once `Son 4 hafta (toplu)` secili kalsin.
   - Rapor uret.
   - Logda stabil bolge ve leave-one-round-out uyarisini oku.
6. `Kolon Sayisi` degerini 100 ustune cikarma.
   - Uygulama zaten 100 kolon / 1.000 TL ust sinir uygular.
7. `CALISTIR` butonuna bas.
8. Logda su satirlari ozellikle kontrol et:
   - `Ogrenilmis strateji elemesi`
   - `Guvenilir ogrenilmis strateji tablosu`
   - `Elo walk-forward`
   - `Dixon-Coles walk-forward`
   - `Kalibre ensemble walk-forward`
   - `Final sembol dagilimi`
   - `tek sembole yigildi`
9. Cikti dosyalari:
   - `SporTotoFormApp/bin/Debug/net8.0-windows/BestScoreCoupon.txt`
   - `SporTotoFormApp/bin/Debug/net8.0-windows/Kuponlar.xlsx`

## Oynama Karari

Oynama tarafinda su kuralı uygula:

- Logda guvenilir ogrenilmis strateji bulunursa ve final sembol dagilimi asiri bozuk degilse, kuponlari degerlendir.
- Guvenilir strateji yoksa veya cok sayida `tek sembole yigildi` uyarisi varsa, o haftayi para harcamadan test run olarak kaydet.
- `BestScoreCoupon.txt` icindeki kuponlar oynanacak sade 15 karakterlik satirlardir.

## Bu Haftaki Basari Kriteri

Projeyi degerlendirirken tek kriter 15/15 olmasin:

- 100 kolon altinda en iyi kupon 13+ ise model hala sinyal uretiyor olabilir.
- 14+ gelirse ama 15 gelmezse parametre/portfoy tarafinda devam etmeye deger.
- 12 ve alti kalirsa ve logda kalibrasyon/dagilim uyarilari varsa, para harcayan modu durdur.

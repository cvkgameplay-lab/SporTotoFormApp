using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SporTotoFormApp.Services
{
    public class PredictionListHelper
    {
        private const int MacSayisi = 15;
        private const int MaxArdisikLimit = 3; // 4. aynı sonucu engeller

        public List<string> FiltreliUret()
        {
            List<string> sonuclar = new List<string>();
            Generate(sonuclar, new StringBuilder(), 0);
            return sonuclar;
        }

        private void Generate(List<string> liste, StringBuilder mevcut, int derinlik)
        {
            if (derinlik == MacSayisi)
            {
                liste.Add(mevcut.ToString());
                return;
            }

            foreach (char secenek in new[] { '1', 'X', '2' })
            {
                if (IsLimitAsildi(mevcut, secenek))
                    continue;

                mevcut.Append(secenek);
                Generate(liste, mevcut, derinlik + 1);
                mevcut.Remove(mevcut.Length - 1, 1); // Backtracking (Geri adım)
            }
        }

        private bool IsLimitAsildi(StringBuilder mevcut, char yeniSecenek)
        {
            if (mevcut.Length < MaxArdisikLimit) return false;

            // Son 3 karakter yeni gelenle aynı mı kontrol et
            for (int i = 1; i <= MaxArdisikLimit; i++)
            {
                if (mevcut[mevcut.Length - i] != yeniSecenek)
                    return false;
            }
            return true;
        }

    }
}

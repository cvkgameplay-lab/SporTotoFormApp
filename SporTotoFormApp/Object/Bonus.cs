using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SporTotoFormApp.Object
{
    public class Bonus
    {
        public string i15 {  get; set; }
        public string i14 {  get; set; }
        public string i13 {  get; set; }
        public string i12 {  get; set; }

    }
    public class doubleBonus
    {
        public double i15 { get; set; }
        public double i14 { get; set; }
        public double i13 { get; set; }
        public double i12 { get; set; }

    }
    public class Coupon
    {
        public string prediction { get; set; }
        public Bonus bonus { get; set; }

    }

    public class FilteredCoupon
    {
        public string prediction { get; set; }
        public doubleBonus bonus { get; set; }

    }
}

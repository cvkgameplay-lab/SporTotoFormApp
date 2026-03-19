namespace SporTotoFormApp.Object
{
    public class Bonus
    {
        public string i15 { get; set; } = "0";
        public string i14 { get; set; } = "0";
        public string i13 { get; set; } = "0";
        public string i12 { get; set; } = "0";
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
        public string prediction { get; set; } = string.Empty;
        public Bonus bonus { get; set; } = new();
        public double Utility { get; set; }
        public double P15Probability { get; set; }
        public double P14Probability { get; set; }
        public double P13Probability { get; set; }
    }

    public class FilteredCoupon
    {
        public string prediction { get; set; } = string.Empty;
        public doubleBonus bonus { get; set; } = new();
    }
}

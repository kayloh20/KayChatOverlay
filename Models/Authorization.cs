namespace KayChatOverlayWPF.Models
{
    internal class Authorization
    {
        public string Code { get; }

        public Authorization(string code)
        {
            Code = code;
        }
    }
}

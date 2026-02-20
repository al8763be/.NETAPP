namespace WebApplication2.Models
{
    public class ContestLeaderboardViewModel
    {
        public Contest Contest { get; set; } = null!;
        public List<ContestEntry> Entries { get; set; } = new();
    }
}

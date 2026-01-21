namespace MMMapEditor
{
    public class OvrObject
    {
        public byte X { get; set; }
        public byte Y { get; set; }
        public byte DirectionByte { get; set; }

        // Словарь для хранения текстов по путям: путь -> список текстов
        public Dictionary<int, HashSet<string>> PathTexts { get; set; } = new Dictionary<int, HashSet<string>>();

        // Свойство, которое говорит, нужно ли показывать префикс Path0
        public bool ShouldShowPath0
        {
            get
            {
                if (!PathTexts.ContainsKey(0) || PathTexts[0].Count == 0)
                    return false;

                // Проверяем, есть ли уникальные альтернативные пути
                return PathTexts.Any(kvp => kvp.Key != 0 && kvp.Value.Count > 0);
            }
        }

        // Получение всех направлений с сообщениями
        public List<Direction> GetDirectionsWithMessages()
        {
            var directions = new List<Direction>();

            for (int i = 0; i < 4; i++)
            {
                int mask = 0x3 << (i * 2);
                bool hasMessage = (DirectionByte & mask) == mask;

                if (hasMessage)
                {
                    Direction direction = i switch
                    {
                        0 => Direction.Bottom,
                        1 => Direction.Left,
                        2 => Direction.Right,
                        3 => Direction.Top,
                        _ => Direction.Top
                    };
                    directions.Add(direction);
                }
            }

            return directions;
        }
    }
}
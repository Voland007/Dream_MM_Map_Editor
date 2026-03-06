using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Drawing;

namespace MMMapEditor.Tests
{
    /// <summary>
    /// Конвертер для сериализации Point в JSON и обратно
    /// </summary>
    public class PointConverter : JsonConverter<Point>
    {
        public override Point ReadJson(JsonReader reader, Type objectType, Point existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            if (reader.TokenType == JsonToken.String)
            {
                string value = reader.Value.ToString();
                // Ожидаемый формат: "(x,y)" или "x,y"
                value = value.Trim('(', ')');
                var parts = value.Split(',');
                if (parts.Length == 2 &&
                    int.TryParse(parts[0].Trim(), out int x) &&
                    int.TryParse(parts[1].Trim(), out int y))
                {
                    return new Point(x, y);
                }
            }
            return default;
        }

        public override void WriteJson(JsonWriter writer, Point value, JsonSerializer serializer)
        {
            writer.WriteValue($"({value.X},{value.Y})");
        }
    }

    /// <summary>
    /// Конвертер для словаря с ключами Point
    /// </summary>
    public class PointDictionaryConverter : JsonConverter<Dictionary<Point, CellExpectation>>
    {
        public override Dictionary<Point, CellExpectation> ReadJson(JsonReader reader, Type objectType,
            Dictionary<Point, CellExpectation> existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            var result = new Dictionary<Point, CellExpectation>();

            if (reader.TokenType == JsonToken.StartObject)
            {
                var obj = JObject.Load(reader);

                foreach (var property in obj.Properties())
                {
                    try
                    {
                        // Парсим ключ вида "(8,3)" или "8,3"
                        string key = property.Name.Trim('(', ')');
                        var parts = key.Split(',');
                        if (parts.Length == 2 &&
                            int.TryParse(parts[0].Trim(), out int x) &&
                            int.TryParse(parts[1].Trim(), out int y))
                        {
                            var point = new Point(x, y);

                            // Десериализуем значение
                            var value = property.Value.ToObject<CellExpectation>(serializer);

                            // Убеждаемся, что ExpectedTexts не null
                            if (value.ExpectedTexts == null)
                                value.ExpectedTexts = new List<string>();

                            result[point] = value;
                        }
                        else
                        {
                            System.Diagnostics.Debug.WriteLine($"Не удалось распарсить ключ: {property.Name}");
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Ошибка при парсинге свойства {property.Name}: {ex.Message}");
                    }
                }
            }

            return result;
        }

        public override void WriteJson(JsonWriter writer, Dictionary<Point, CellExpectation> value, JsonSerializer serializer)
        {
            writer.WriteStartObject();

            foreach (var kvp in value)
            {
                writer.WritePropertyName($"({kvp.Key.X},{kvp.Key.Y})");
                serializer.Serialize(writer, kvp.Value);
            }

            writer.WriteEndObject();
        }
    }
}
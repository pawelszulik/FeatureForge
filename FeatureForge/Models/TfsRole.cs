using System.Text.Json.Serialization;

namespace FeatureForge.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TfsRole
{
    FeatureDoc, // PM, Analityk, Architekt, Tech Lead → opis Feature
    Task,       // Backend, Frontend, DevOps, Security, QA, Tech Writer → Task
    UserStory   // UX Designer → User Story
}

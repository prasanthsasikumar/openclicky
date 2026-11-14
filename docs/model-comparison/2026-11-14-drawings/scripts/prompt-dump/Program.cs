using OpenClicky.Core.Plugins.DrawStage;
var outDir = args[0];
File.WriteAllText(Path.Combine(outDir, "system.txt"), DrawStageLesson.SystemPrompt(1280, 720, DrawingDetail.Detailed, null, pieces: false, languageCode: null, answerStyle: null, pictures: false));
foreach (var (id, q) in new[] { ("school", "make me a 3D model of a school"), ("skull", "show me a 3D model of a human skull"), ("plane", "make me a 3D model of an airplane") })
    File.WriteAllText(Path.Combine(outDir, $"user-{id}.txt"), DrawStageLesson.UserPrompt(q));
File.WriteAllText(Path.Combine(outDir, "maxtokens.txt"), DrawStageLesson.OutputTokens(DrawingDetail.Detailed).ToString());
Console.WriteLine("ok");

using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DevAi
{
    public class AiAgent
    {
        private readonly HttpClient _httpClient;
        private const string OllamaEndpoint = "http://localhost:11434/api/generate";
        private const string ModelName = "deepseek-coder";

        public AiAgent()
        {
            _httpClient = new HttpClient();
            // Increase timeout to 10 minutes to handle large code generation tasks
            // or slower hardware (CPU fallback) gracefully.
            _httpClient.Timeout = TimeSpan.FromMinutes(10);
        }

        public async Task<string> GetResponseAsync(string userPrompt, string codeContext, string filePath = "CurrentFile.cs")
        {
            string language = "Unknown";
            if (filePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) language = "C#";
            else if (filePath.EndsWith(".py", StringComparison.OrdinalIgnoreCase)) language = "Python";
            else if (filePath.EndsWith(".js", StringComparison.OrdinalIgnoreCase)) language = "JavaScript";
            else if (filePath.EndsWith(".html", StringComparison.OrdinalIgnoreCase)) language = "HTML";
            else if (filePath.EndsWith(".css", StringComparison.OrdinalIgnoreCase)) language = "CSS";
            else if (filePath.EndsWith(".java", StringComparison.OrdinalIgnoreCase)) language = "Java";
            else if (filePath.EndsWith(".cpp", StringComparison.OrdinalIgnoreCase)) language = "C++";

            try
            {
                var fullPrompt = $"You are an intelligent coding assistant embedded inside an IDE.\n" +
$"You are currently viewing the file located at: {filePath}\n" +
$"The detected language is: {language}\n\n" +

$"Your task is to analyze the provided code context and answer the user's question.\n\n" +

$"LANGUAGE RULES:\n" +
$"- You MUST respond strictly in {language}.\n" +
$"- Always follow standard formatting and indentation conventions for {language}.\n\n" +

$"CRITICAL INSTRUCTIONS:\n" +
$"- ALWAYS return the COMPLETE, FULLY FORMATTED source code for the file.\n" +
$"- Even if the user asks a simple question, you MUST return the entire file.\n" +
$"- Do NOT omit, truncate, or summarize any part of the code.\n" +
$"- Ensure all blocks, scopes, and structures are properly closed.\n" +
$"- Do NOT add unnecessary comments explaining the code structure or behavior.\n" +
$"- Do Not add any sort of commentary outside the code. and inside the code\n" +
$"- Do NOT include meta commentary, self-references, or provider-specific guidelines.\n" +
$"- If the task can be completed without comments, return code with ZERO comments.\n" +
$"- Keep the code clean, minimal, idiomatic, and production-ready.\n\n" +

$"OUTPUT FORMAT:\n" +
$"- Wrap the entire response in a single Markdown code block.\n" +
$"- Use the appropriate language identifier (```python, ```csharp, etc.).\n\n" +

$"CODE CONTEXT:\n{codeContext}\n\n" +

$"USER QUESTION:\n{userPrompt}\n\n" +

$"ANSWER:";



                var requestBody = new
                {
                    model = ModelName,
                    prompt = fullPrompt,
                    stream = false
                };

                var json = JsonConvert.SerializeObject(requestBody);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var response = await _httpClient.PostAsync(OllamaEndpoint, content);
                response.EnsureSuccessStatusCode();

                var responseString = await response.Content.ReadAsStringAsync();
                
                // Debugging: Return raw response if it's short, or try to parse
                if (string.IsNullOrWhiteSpace(responseString))
                {
                    return "Error: Received empty response from Ollama.";
                }

                try 
                {
                    var jsonResponse = JObject.Parse(responseString);
                    var responseText = jsonResponse["response"]?.ToString();
                    
                    if (string.IsNullOrWhiteSpace(responseText))
                    {
                        return $"Error: Parsed 'response' property is empty. Raw JSON: {responseString}";
                    }

                    return responseText;
                }
                catch (JsonReaderException)
                {
                    // It might be returning multiple JSON objects (streaming mode artifact)
                    // or plain text. Return raw string for debugging.
                    return $"Error: Could not parse JSON. Raw output: {responseString}";
                }
            }
            catch (Exception ex)
            {
                return $"Error communicating with Ollama: {ex.Message}. Ensure Ollama is running.";
            }
        }
    }
}

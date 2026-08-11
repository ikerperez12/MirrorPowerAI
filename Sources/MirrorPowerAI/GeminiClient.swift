import Foundation

enum GeminiError: LocalizedError {
    case missingAPIKey
    case badResponse(String)

    var errorDescription: String? {
        switch self {
        case .missingAPIKey:
            return "Configura tu Gemini API Key desde el menú."
        case .badResponse(let message):
            return message
        }
    }
}

final class GeminiClient {
    private let model = "gemini-3.5-flash"

    private var endpoint: URL {
        URL(string: "https://generativelanguage.googleapis.com/v1beta/models/\(model):generateContent")!
    }

    func ask(question: String, context: String?, completion: @escaping (Result<String, Error>) -> Void) {
        guard let apiKey = KeychainHelper.loadAPIKey(), !apiKey.isEmpty else {
            completion(.failure(GeminiError.missingAPIKey))
            return
        }

        var systemPrompt = """
        Estás ayudando a alguien que está haciendo una demo en directo delante de compañeros. \
        Acabas de recibir la transcripción de una pregunta que le han hecho durante la demo. \
        Responde de forma breve, concreta y técnica, como si fueran apuntes rápidos para que \
        la persona pueda contestar con seguridad. No repitas la pregunta, ve directo a la respuesta.
        """
        if let context, !context.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            systemPrompt += "\n\nContexto del sistema que se está enseñando:\n\(context)"
        }

        let body: [String: Any] = [
            "system_instruction": [
                "parts": [["text": systemPrompt]]
            ],
            "contents": [
                ["role": "user", "parts": [["text": question]]]
            ]
        ]

        var request = URLRequest(url: endpoint)
        request.httpMethod = "POST"
        request.setValue(apiKey, forHTTPHeaderField: "x-goog-api-key")
        request.setValue("application/json", forHTTPHeaderField: "content-type")
        request.httpBody = try? JSONSerialization.data(withJSONObject: body)

        URLSession.shared.dataTask(with: request) { data, response, error in
            if let error {
                completion(.failure(error))
                return
            }
            guard let data else {
                completion(.failure(GeminiError.badResponse("Sin datos en la respuesta")))
                return
            }
            do {
                guard let json = try JSONSerialization.jsonObject(with: data) as? [String: Any] else {
                    completion(.failure(GeminiError.badResponse("Respuesta no válida")))
                    return
                }
                if let errorObj = json["error"] as? [String: Any], let message = errorObj["message"] as? String {
                    completion(.failure(GeminiError.badResponse(message)))
                    return
                }
                guard let candidates = json["candidates"] as? [[String: Any]],
                      let content = candidates.first?["content"] as? [String: Any],
                      let parts = content["parts"] as? [[String: Any]],
                      let text = parts.first?["text"] as? String else {
                    completion(.failure(GeminiError.badResponse("Formato de respuesta inesperado")))
                    return
                }
                completion(.success(text))
            } catch {
                completion(.failure(error))
            }
        }.resume()
    }
}

//! Bounded UTF-8 JSON-lines transport shared by the native UI and Rust host.
//! stdout belongs exclusively to this protocol; diagnostics use stderr.
use crate::error::{AppError, Result};
use serde::Deserialize;
use serde_json::{Map, Value};
use std::io::{BufRead, Write};

// System.Text.Json may encode one ASCII byte (for example '<') as six bytes in
// a Unicode escape. Permit a full-size YAML document under that worst case,
// while bounding the encoded frame before allocating an unbounded line.
pub const MAX_FRAME_BYTES: usize = crate::config::MAX_CONFIG_SIZE * 6 + 4096;

#[derive(Debug, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Request {
    pub id: i64,
    pub method: String,
    #[serde(default)]
    pub params: Map<String, Value>,
}

pub fn read_frame(reader: &mut impl BufRead) -> Result<Option<Vec<u8>>> {
    let mut bytes = Vec::new();
    loop {
        let available = reader
            .fill_buf()
            .map_err(|e| AppError::io("无法读取桌面请求", e))?;
        if available.is_empty() {
            if bytes.is_empty() {
                return Ok(None);
            }
            // A truncated mutation must never execute after its owner exited.
            return Err(AppError::new("protocol_truncated", "桌面请求未完整发送"));
        }
        let end = available.iter().position(|byte| *byte == b'\n');
        let consumed = end.map_or(available.len(), |index| index + 1);
        if bytes.len().saturating_add(consumed) > MAX_FRAME_BYTES {
            return Err(AppError::new("protocol_too_large", "桌面请求超过大小限制"));
        }
        bytes.extend_from_slice(&available[..consumed]);
        reader.consume(consumed);
        if end.is_some() {
            bytes.pop();
            if bytes.last() == Some(&b'\r') {
                bytes.pop();
            }
            return Ok(Some(bytes));
        }
    }
}

pub fn parse_request(bytes: &[u8]) -> Result<Request> {
    let request: Request = serde_json::from_slice(bytes)
        .map_err(|_| AppError::new("protocol_invalid", "无效的桌面请求"))?;
    if request.method.is_empty()
        || request.method.len() > 96
        || !request
            .method
            .bytes()
            .all(|byte| byte.is_ascii_lowercase() || byte == b'_')
    {
        return Err(AppError::new("protocol_invalid", "无效的桌面操作名称"));
    }
    Ok(request)
}

/// Invalid requests can still identify the pending caller without echoing YAML,
/// credentials, URLs, or parser input in a diagnostic.
pub fn request_id(bytes: &[u8]) -> Option<i64> {
    serde_json::from_slice::<Value>(bytes)
        .ok()?
        .get("id")?
        .as_i64()
}

pub fn write_frame(writer: &mut impl Write, value: &Value) -> std::io::Result<()> {
    serde_json::to_writer(&mut *writer, value)?;
    writer.write_all(b"\n")?;
    writer.flush()
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::io::{BufReader, Cursor};

    #[test]
    fn fragmented_frames_and_escaped_multiline_yaml_preserve_boundaries() {
        let first = serde_json::json!({"id":1,"method":"desktop_save_config","params":{"content":"# 中文\nproxies: []\n","expectedRevision":"abc"}});
        let second = serde_json::json!({"id":2,"method":"desktop_status","params":{}});
        let mut stream = Vec::new();
        write_frame(&mut stream, &first).unwrap();
        write_frame(&mut stream, &second).unwrap();
        let mut input = BufReader::with_capacity(3, Cursor::new(stream));
        let request = parse_request(&read_frame(&mut input).unwrap().unwrap()).unwrap();
        assert_eq!(request.id, 1);
        assert_eq!(request.params["content"], "# 中文\nproxies: []\n");
        assert_eq!(
            parse_request(&read_frame(&mut input).unwrap().unwrap())
                .unwrap()
                .id,
            2
        );
        assert!(read_frame(&mut input).unwrap().is_none());
    }

    #[test]
    fn incomplete_or_oversized_frames_are_never_executed() {
        let mut truncated = Cursor::new(br#"{"id":1,"method":"desktop_start"}"#);
        assert_eq!(
            read_frame(&mut truncated).unwrap_err().code,
            "protocol_truncated"
        );
        let mut excessive = Cursor::new(vec![b'x'; MAX_FRAME_BYTES + 1]);
        assert_eq!(
            read_frame(&mut excessive).unwrap_err().code,
            "protocol_too_large"
        );
    }

    #[test]
    fn full_size_config_accepts_system_text_json_worst_case_escaping() {
        let prefix = "#";
        let suffix = "\nmode: rule\n";
        let content = format!(
            "{prefix}{}{suffix}",
            "<".repeat(crate::config::MAX_CONFIG_SIZE - prefix.len() - suffix.len())
        );
        let request = serde_json::json!({
            "id": 1,
            "method": "desktop_save_config",
            "params": {"content": content, "expectedRevision": "abc"}
        });
        let mut encoded = serde_json::to_string(&request)
            .unwrap()
            .replace('<', "\\u003C");
        encoded.push('\n');
        let mut input = BufReader::new(Cursor::new(encoded));
        let parsed = parse_request(&read_frame(&mut input).unwrap().unwrap()).unwrap();
        assert_eq!(parsed.params["content"], content);
        assert!(read_frame(&mut input).unwrap().is_none());
    }

    #[test]
    fn untrusted_fields_types_and_invalid_utf8_are_rejected_without_echoing_secrets() {
        for bytes in [
            br#"{"id":1,"method":"desktop_start","params":null}"#.as_slice(),
            br#"{"id":"secret-value","method":"desktop_start"}"#,
            br#"{"id":1,"method":"desktop_start","extra":"secret-value"}"#,
            br#"{"id":1,"method":"desktop_start\nsecret-value"}"#,
            &[0xff, 0xfe],
        ] {
            let error = parse_request(bytes).unwrap_err();
            assert_eq!(error.code, "protocol_invalid");
            assert!(!error.message.contains("secret-value"));
        }
        assert_eq!(
            request_id(br#"{"id":9223372036854775807,"method":null}"#),
            Some(i64::MAX)
        );
        assert!(parse_request(br#"{"id":9223372036854775808,"method":"desktop_status"}"#).is_err());
    }
}

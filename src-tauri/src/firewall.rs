use crate::{
    config,
    error::{AppError, Result},
    windows,
};
use sha2::{Digest, Sha256};
use std::fs;
use windows_api::{
    core::{BSTR, HRESULT},
    Win32::{
        Foundation::{RPC_E_CHANGED_MODE, VARIANT_FALSE, VARIANT_TRUE},
        NetworkManagement::WindowsFirewall::{
            INetFwPolicy2, INetFwRule, INetFwRules, NetFwPolicy2, NetFwRule, NET_FW_ACTION_ALLOW,
            NET_FW_IP_PROTOCOL_TCP, NET_FW_PROFILE2_ALL, NET_FW_RULE_DIR_IN,
        },
        System::Com::{
            CoCreateInstance, CoInitializeEx, CoUninitialize, CLSCTX_INPROC_SERVER,
            COINIT_MULTITHREADED,
        },
    },
};

const RULE_GROUP: &str = "Mihomo Optimized TUN";
const NOT_FOUND: HRESULT = HRESULT(0x80070002_u32 as i32);

struct ComApartment(bool);

impl Drop for ComApartment {
    fn drop(&mut self) {
        if self.0 {
            unsafe { CoUninitialize() };
        }
    }
}

// Fixed stage names and HRESULTs make failed or blocked helper operations
// diagnosable without writing configuration, controller secrets or COM messages.
fn record(stage: &str, code: &str, hresult: Option<HRESULT>) {
    let _ = (|| -> Result<()> {
        let data = windows::service_data_dir()?;
        windows::reject_reparse_path(&data)?;
        windows::require_admin_protected(&data)?;
        let path = data.join("firewall-status.json");
        if path.exists() {
            windows::reject_reparse_path(&path)?;
            windows::reject_hard_links(&path)?;
        }
        let value = serde_json::json!({
            "stage": stage,
            "code": code,
            "hresult": hresult.map(|value| format!("0x{:08X}", value.0 as u32)),
        });
        config::atomic_write(&path, &serde_json::to_vec(&value).unwrap_or_default())?;
        let owner_path = data.join("owner.sid");
        windows::reject_reparse_path(&owner_path)?;
        windows::require_admin_protected(&owner_path)?;
        if let Ok(owner) = fs::read_to_string(&owner_path) {
            let owner = owner.trim();
            if owner.starts_with("S-1-5-21-")
                && owner.len() < 180
                && owner
                    .split('-')
                    .skip(1)
                    .all(|part| !part.is_empty() && part.bytes().all(|byte| byte.is_ascii_digit()))
            {
                windows::secure_admin_tree(&path, Some(owner))?;
            }
        }
        Ok(())
    })();
}

fn call<T>(stage: &str, operation: impl FnOnce() -> windows_api::core::Result<T>) -> Result<T> {
    record(stage, "in_progress", None);
    operation().map_err(|error| {
        let code = format!("firewall_{stage}");
        record(stage, &code, Some(error.code()));
        AppError::new(
            &code,
            format!(
                "TUN 防火墙操作失败（{stage}，HRESULT 0x{:08X}）",
                error.code().0 as u32
            ),
        )
    })
}

fn lookup(rules: &INetFwRules, name: &BSTR) -> Result<Option<INetFwRule>> {
    record("lookup", "in_progress", None);
    match unsafe { rules.Item(name) } {
        Ok(rule) => Ok(Some(rule)),
        Err(error) if error.code() == NOT_FOUND => Ok(None),
        Err(error) => call("lookup", || Err(error)),
    }
}

fn program_matches(actual: &str, expected: &str) -> bool {
    actual
        .strip_prefix(r"\\?\")
        .unwrap_or(actual)
        .to_lowercase()
        == expected.to_lowercase()
}

pub fn configure(program: &str, install: bool) -> Result<()> {
    record("initialize", "in_progress", None);
    let initialized = unsafe { CoInitializeEx(None, COINIT_MULTITHREADED) };
    if initialized.is_err() && initialized != RPC_E_CHANGED_MODE {
        return call("initialize", || initialized.ok());
    }
    // Declared first so every COM interface is released before uninitialization.
    let _apartment = ComApartment(initialized.is_ok());
    let policy: INetFwPolicy2 = call("create_policy", || unsafe {
        CoCreateInstance(&NetFwPolicy2, None, CLSCTX_INPROC_SERVER)
    })?;
    let rules = call("read_rules", || unsafe { policy.Rules() })?;
    let digest = hex::encode(Sha256::digest(program.to_lowercase().as_bytes()));
    let name = BSTR::from(format!(
        "com.chummumm.mihomo-optimized.tun.tcp.{}",
        &digest[..16]
    ));
    let existing = lookup(&rules, &name)?;
    if let Some(rule) = &existing {
        let application = call("read_program", || unsafe { rule.ApplicationName() })?.to_string();
        let group = call("read_group", || unsafe { rule.Grouping() })?.to_string();
        if !program_matches(&application, program) || group != RULE_GROUP {
            record("ownership", "firewall_rule_conflict", None);
            return Err(AppError::new(
                "firewall_rule_conflict",
                "同名 TUN 防火墙规则属于其他程序，请在 Windows 防火墙中检查",
            ));
        }
    }
    if install {
        let is_new = existing.is_none();
        let rule = match existing {
            Some(rule) => rule,
            None => call("create_rule", || unsafe {
                CoCreateInstance::<_, INetFwRule>(&NetFwRule, None, CLSCTX_INPROC_SERVER)
            })?,
        };
        call("set_name", || unsafe { rule.SetName(&name) })?;
        call("set_program", || unsafe {
            rule.SetApplicationName(&BSTR::from(program))
        })?;
        call("set_group", || unsafe {
            rule.SetGrouping(&BSTR::from(RULE_GROUP))
        })?;
        call("set_description", || unsafe {
            rule.SetDescription(&BSTR::from("TCP system-stack access for the protected bundled Mihomo core, managed with its TUN service."))
        })?;
        // Protocol must be selected before port properties are set.
        call("set_protocol", || unsafe {
            rule.SetProtocol(NET_FW_IP_PROTOCOL_TCP.0)
        })?;
        call("set_local_ports", || unsafe {
            rule.SetLocalPorts(&BSTR::from("*"))
        })?;
        call("set_remote_ports", || unsafe {
            rule.SetRemotePorts(&BSTR::from("*"))
        })?;
        call("set_local_addresses", || unsafe {
            rule.SetLocalAddresses(&BSTR::from("*"))
        })?;
        call("set_remote_addresses", || unsafe {
            rule.SetRemoteAddresses(&BSTR::from("*"))
        })?;
        call("set_interfaces", || unsafe {
            rule.SetInterfaceTypes(&BSTR::from("All"))
        })?;
        call("set_direction", || unsafe {
            rule.SetDirection(NET_FW_RULE_DIR_IN)
        })?;
        call("set_profiles", || unsafe {
            rule.SetProfiles(NET_FW_PROFILE2_ALL.0)
        })?;
        call("set_edge_traversal", || unsafe {
            rule.SetEdgeTraversal(VARIANT_FALSE)
        })?;
        call("set_action", || unsafe {
            rule.SetAction(NET_FW_ACTION_ALLOW)
        })?;
        call("set_enabled", || unsafe { rule.SetEnabled(VARIANT_TRUE) })?;
        if is_new {
            call("add_rule", || unsafe { rules.Add(&rule) })?;
        }
        let verified = call("verify_rule", || unsafe { rules.Item(&name) })?;
        let actual = call("verify_program", || unsafe { verified.ApplicationName() })?.to_string();
        let valid = actual.eq_ignore_ascii_case(program)
            && call("verify_enabled", || unsafe { verified.Enabled() })? == VARIANT_TRUE
            && call("verify_direction", || unsafe { verified.Direction() })? == NET_FW_RULE_DIR_IN
            && call("verify_action", || unsafe { verified.Action() })? == NET_FW_ACTION_ALLOW;
        if !valid {
            record("verify", "firewall_rule_unverified", None);
            return Err(AppError::new(
                "firewall_rule_unverified",
                "无法确认本程序的 TUN 防火墙规则",
            ));
        }
    } else {
        if existing.is_some() {
            call("remove_rule", || unsafe { rules.Remove(&name) })?;
        }
        // Include the earlier desktop draft's friendly name, and sing-tun's
        // automatic rules keyed by argv[0]. Name, group and program must match.
        for (automatic_name, expected_group) in [
            ("Mihomo Optimized TUN TCP".to_owned(), RULE_GROUP),
            (format!("sing-tun ({program})"), ""),
            (format!(r"sing-tun (\\?\{program})"), ""),
        ] {
            let automatic_name = BSTR::from(automatic_name);
            if let Some(rule) = lookup(&rules, &automatic_name)? {
                let application =
                    call("read_auto_program", || unsafe { rule.ApplicationName() })?.to_string();
                let group = call("read_auto_group", || unsafe { rule.Grouping() })?.to_string();
                if program_matches(&application, program)
                    && group == expected_group
                    && call("read_auto_protocol", || unsafe { rule.Protocol() })?
                        == NET_FW_IP_PROTOCOL_TCP.0
                    && call("read_auto_direction", || unsafe { rule.Direction() })?
                        == NET_FW_RULE_DIR_IN
                    && call("read_auto_action", || unsafe { rule.Action() })? == NET_FW_ACTION_ALLOW
                {
                    call("remove_auto_rule", || unsafe {
                        rules.Remove(&automatic_name)
                    })?;
                }
            }
        }
    }
    record("complete", "ok", None);
    Ok(())
}

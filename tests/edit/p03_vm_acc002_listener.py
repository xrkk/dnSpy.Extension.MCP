#!/usr/bin/env python3
"""Listener-only continuation for T016 ACC-002/019.

The separate EDIT-ACC-002-LIFECYCLE route owns the real 600-second and DELETE
checks. This route keeps listener/stdio/port scenarios independently runnable.
"""
from __future__ import annotations

import json

import p03_vm_acc002_lifecycle as impl

DnSpyClient = impl.DnSpyClient
call = impl.call


def configure_isolation(context) -> None:
    impl.configure_isolation(context)


def main() -> int:
    impl.DnSpyClient = DnSpyClient
    impl.call = call
    impl.FAILURES.clear()
    impl.PASSES.clear()
    impl.SIGNAL_SEQUENCE = 0
    original_url = impl.URL
    original_port = int(original_url.rsplit(":", 1)[1].split("/", 1)[0])
    alternate_port = original_port + 1
    transcript: list[dict] = []
    open_values: list = []
    current_port = original_port
    try:
        bootstrap = impl.new_client("t016-listener-bootstrap")
        opened = impl.call(bootstrap, "open_files", {"paths": [impl.FIXTURE]})
        impl.check("K0 fixture opened", int(opened.get("loaded_count", 0)) == 1
                   and int(opened.get("failed_count", 0)) == 0, opened)
        impl.close_quietly(bootstrap)

        seed = impl.new_client("t016-listener-seed")
        committed = impl.commit_name(seed, "T016_CommittedSeed")
        lineage_id = str(impl.payload(committed).get("history", {}).get("lineage_id", ""))
        checkpoint_id = str(impl.payload(committed).get("checkpoint", {}).get("checkpoint_id", ""))
        impl.check("K3 committed seed created", bool(committed.get("ok"))
                   and bool(lineage_id) and bool(checkpoint_id), committed)
        impl.close_quietly(seed)

        bridge = impl.StdioBridge(original_url, transcript)
        _, bridge_context = bridge.tool("debug_test_transport", {"p01_action": "snapshot"})
        bridge_session = str(impl.payload(bridge_context).get("authoritative_session_id", ""))
        sessions = [impl.new_client(f"t016-quota-before-{i}") for i in range(15)]
        open_values.extend(sessions)
        old_direct_sessions = {str(item.session_id) for item in sessions}
        overflow_refused = False
        try:
            overflow = impl.new_client("t016-quota-before-overflow")
            impl.close_quietly(overflow)
        except Exception:  # noqa: BLE001
            overflow_refused = True
        restart_owner = sessions[0]
        restart_owner_session = str(restart_owner.session_id)
        restart_tx, restart_revision, _ = impl.begin_apply(restart_owner, "T016_ListenerPending")
        ack1 = impl.request_listener("restart_same_url", original_port)
        stale_after_restart = impl.call(restart_owner, "edit_apply", {
            "request_id": impl.rid("stale-listener"), "transaction_id": restart_tx,
            "expected_revision": restart_revision,
            "operation": {"kind": "type_update", "target": {"token": "0x02000002"},
                          "name": "T016_ListenerMustFail"},
        })
        bridge_pid = bridge.pid
        automatic_raw, automatic_status = bridge.tool("edit_status", {})
        explicit_recovery_used = "error" in automatic_raw
        explicit_initialize = None
        if explicit_recovery_used:
            explicit_initialize = bridge.initialize()
        for item in sessions:
            impl.close_quietly(item)
        open_values.clear()

        _, new_context = bridge.tool("debug_test_transport", {"p01_action": "snapshot"})
        new_bridge_session = str(impl.payload(new_context).get("authoritative_session_id", ""))
        _, status_via_bridge = bridge.tool("edit_status", {})
        history_raw, history_via_bridge = bridge.tool("edit_history", {
            "lineage_id": lineage_id, "page_size": 100,
        })
        checkpoints = impl.payload(history_via_bridge).get("checkpoints", [])
        committed_visible = any(isinstance(row, dict) and row.get("checkpoint_id") == checkpoint_id
                                for row in checkpoints)
        lifecycle = impl.payload(new_context).get("lifecycle", {})
        events = lifecycle.get("events", []) if isinstance(lifecycle, dict) else []
        stopped_ids = {str(row.get("session_id")) for row in events
                       if isinstance(row, dict) and row.get("reason") == "listener_stop"}
        listener_zero = any(isinstance(row, dict) and row.get("reason") == "listener_stop"
                            and row.get("active_session_count_after_removal") == 0 for row in events)
        impl.check("K1 listener restart rolls back uncommitted transaction",
                   impl.payload(status_via_bridge).get("state") == "idle", status_via_bridge)
        impl.check("K2 same-process stdio bridge automatically reinitializes",
                   impl.same_process_auto_recovery_pass(
                       old_session=bridge_session, new_session=new_bridge_session,
                       pid_before=bridge_pid, pid_after=bridge.pid,
                       automatic_raw=automatic_raw, automatic_status=automatic_status,
                       explicit_recovery_used=explicit_recovery_used)
                   and history_raw.get("result") is not None,
                   {"old_session": bridge_session, "new_session": new_bridge_session,
                    "bridge_pid_before": bridge_pid, "bridge_pid_after": bridge.pid,
                    "automatic_read": automatic_raw,
                    "explicit_recovery_used": explicit_recovery_used,
                    "explicit_initialize": explicit_initialize})
        impl.check("K3 old sessions invalid and listener stop drains to zero",
                   overflow_refused and not stale_after_restart.get("ok")
                   and old_direct_sessions.issubset(stopped_ids)
                   and restart_owner_session in stopped_ids and bridge_session in stopped_ids and listener_zero,
                   {"overflow_refused": overflow_refused, "stale": stale_after_restart,
                    "expected_old_sessions": sorted(old_direct_sessions | {bridge_session}),
                    "listener_stop_sessions": sorted(stopped_ids), "ack": ack1})
        impl.check("K3 committed history survives listener stop", committed_visible,
                   {"lineage_id": lineage_id, "checkpoint_id": checkpoint_id,
                    "history": history_via_bridge})

        new_sessions = [impl.new_client(f"t016-quota-after-{i}") for i in range(15)]
        open_values.extend(new_sessions)
        after_overflow_refused = False
        try:
            overflow = impl.new_client("t016-quota-after-overflow")
            impl.close_quietly(overflow)
        except Exception:  # noqa: BLE001
            after_overflow_refused = True
        impl.check("K3 all 16 session slots reusable after listener stop",
                   len(new_sessions) == 15 and after_overflow_refused,
                   {"stdio_sessions": 1, "direct_sessions": len(new_sessions),
                    "seventeenth_refused": after_overflow_refused})
        for item in new_sessions:
            impl.close_quietly(item)
        open_values.clear()
        bridge.close()

        partial_owner = impl.new_client("t016-partial-owner")
        partial = impl.commit_name(partial_owner, "T016_Partial", fault=True)
        partial_before = impl.payload(impl.call(partial_owner, "edit_status", {}))
        recovery_before = partial_before.get("recovery")
        partial_owner_session = str(partial_owner.session_id)
        impl.check("K3 partial seeded", impl.error_code(partial) == "EDIT_CHECKPOINT_COMMIT_FAILED"
                   and partial_before.get("state") == "committed_without_checkpoint"
                   and isinstance(recovery_before, dict), {"commit": partial, "status": partial_before})
        ack2 = impl.request_listener("restart_same_url", original_port)
        stale_partial = impl.call(partial_owner, "edit_status", {})
        impl.close_quietly(partial_owner)
        partial_observer = impl.new_client("t016-partial-observer")
        partial_after = impl.payload(impl.call(partial_observer, "edit_status", {}))
        history_after = impl.payload(impl.call(partial_observer, "edit_history", {
            "lineage_id": lineage_id, "page_size": 100,
        }))
        recovery_after = partial_after.get("recovery")
        same_recovery = (isinstance(recovery_before, dict) and isinstance(recovery_after, dict)
                         and recovery_before.get("recovery_id") == recovery_after.get("recovery_id"))
        impl.check("K3 partial and committed records survive listener stop",
                   not stale_partial.get("ok") and partial_after.get("state") == "committed_without_checkpoint"
                   and same_recovery and any(isinstance(row, dict) and row.get("checkpoint_id") == checkpoint_id
                                             for row in history_after.get("checkpoints", [])),
                   {"owner_session": partial_owner_session, "stale": stale_partial,
                    "before": partial_before, "after": partial_after, "ack": ack2})
        recovered = impl.call(partial_observer, "edit_recover", {
            "request_id": impl.rid("recover"), "recovery_id": recovery_after.get("recovery_id", ""),
            "action": "retry_checkpoint",
        })
        impl.check("K3 fresh session resolves retained partial", bool(recovered.get("ok")), recovered)
        impl.close_quietly(partial_observer)

        response_proxy = impl.ResponseDropProxy(original_url)
        port_bridge = impl.StdioBridge(response_proxy.url, transcript)
        uncertain_bridge_pid = port_bridge.pid
        mutation_id = impl.rid("port-mutation")
        _, port_begin = port_bridge.tool("edit_begin", {
            "request_id": impl.rid("port-begin"), "assembly_name": "TestIL",
        })
        port_tx = str(impl.payload(port_begin).get("transaction", {}).get("transaction_id", ""))
        response_proxy.arm(mutation_id)
        old_apply_raw, old_apply = port_bridge.tool("edit_apply", {
            "request_id": mutation_id, "transaction_id": port_tx, "expected_revision": 0,
            "operation": {"kind": "type_update", "target": {"token": "0x02000002"},
                          "name": "T016_PortMustNotReplay"},
        })
        receipt_before_change = response_proxy.target_events()
        ack3 = impl.request_listener("change_port", alternate_port)
        current_port = alternate_port
        old_url_raw, _ = port_bridge.tool("edit_status", {})
        receipt_after_change = response_proxy.target_events()
        port_bridge.close()
        response_proxy.close()
        new_url = f"http://127.0.0.1:{alternate_port}/mcp"
        new_port_bridge = impl.StdioBridge(new_url, transcript)
        _, new_port_status = new_port_bridge.tool("edit_status", {})
        _, search = new_port_bridge.tool("search_types", {
            "query": "T016_PortMustNotReplay", "assembly_name": "TestIL", "page_size": 20,
        })
        new_url_mutations = [row for row in transcript if row.get("direction") == "request"
                             and row.get("url") == new_url
                             and row.get("message", {}).get("method") == "tools/call"
                             and row.get("message", {}).get("params", {}).get("name") == "edit_apply"]
        impl.check("K2 port change requires explicit URL and does not replay",
                   impl.uncertain_request_pass(
                       ambiguous_raw=old_apply_raw, receipts_before=receipt_before_change,
                       receipts_after=receipt_after_change, old_url_raw=old_url_raw,
                       new_status=new_port_status, search=search,
                       new_url_mutations=new_url_mutations),
                   {"old_url": original_url, "new_url": new_url, "old_url_response": old_url_raw,
                    "new_status": new_port_status, "search": search,
                    "new_url_mutation_requests": new_url_mutations, "mutation_id": mutation_id,
                    "uncertain_bridge_pid": uncertain_bridge_pid,
                    "bridge_configured_url": response_proxy.url,
                    "http_receipt_before_change": receipt_before_change,
                    "http_receipt_after_change": receipt_after_change,
                    "ambiguous_client_response": old_apply_raw, "ack": ack3})
        new_port_bridge.close()
        ack4 = impl.request_listener("restore_port", original_port)
        current_port = original_port
        final = impl.new_client("t016-final", original_url)
        final_status = impl.payload(impl.call(final, "edit_status", {}))
        impl.check("K2 original URL explicitly restored", final_status.get("state") == "idle",
                   {"status": final_status, "ack": ack4})
        impl.close_quietly(final)
        impl.emit("stdio_transcript", transcript)
    except Exception as ex:  # noqa: BLE001
        impl.FAILURES.append("driver_exception")
        impl.emit("driver_exception", {"type": type(ex).__name__, "message": str(ex),
                                       "current_port": current_port})
    finally:
        for value in open_values:
            impl.close_quietly(value)
    impl.emit("summary", {"status": "PASS" if not impl.FAILURES else "FAIL",
                          "passes": impl.PASSES, "failures": impl.FAILURES,
                          "original_url": original_url, "final_port": current_port})
    print("ACC002LISTENER " + ("PASS" if not impl.FAILURES else "FAIL"), flush=True)
    return 0 if not impl.FAILURES else 1


if __name__ == "__main__":
    raise SystemExit(main())

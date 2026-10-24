"""Instance CLI commands for managing Unity instances."""

import time
from pathlib import Path
from typing import Optional

import click

from cli.utils.config import get_config
from cli.utils.output import format_output, print_error, print_success, print_info
from cli.utils.connection import run_command, run_list_instances, handle_unity_errors


@click.group()
def instance():
    """Unity instance management - list, select, and view instances."""
    pass


@instance.command("list")
@handle_unity_errors
def list_instances():
    """List available Unity instances.

    \\b
    Examples:
        unity-mcp instance list
    """
    config = get_config()

    result = run_list_instances(config)
    instances = result.get("instances", []) if isinstance(
        result, dict) else []

    if not instances:
        print_info("No Unity instances currently connected")
        return

    click.echo("Available Unity instances:")
    for inst in instances:
        project = inst.get("project", "Unknown")
        version = inst.get("unity_version", "Unknown")
        hash_id = inst.get("hash", "")
        session_id = inst.get("session_id", "")

        # Format: ProjectName@hash (Unity version)
        display_id = f"{project}@{hash_id}" if hash_id else project
        click.echo(f"  • {display_id} (Unity {version})")
        if session_id:
            click.echo(f"    Session: {session_id[:8]}...")


@instance.command("set")
@click.argument("instance_id")
@handle_unity_errors
def set_instance(instance_id: str):
    """Set the active Unity instance.

    INSTANCE_ID can be Name@hash or just a hash prefix.

    \\b
    Examples:
        unity-mcp instance set "MyProject@abc123"
        unity-mcp instance set abc123
    """
    config = get_config()

    result = run_command("set_active_instance", {
        "instance": instance_id,
    }, config)
    click.echo(format_output(result, config.format))
    if result.get("success"):
        data = result.get("data", {})
        active = data.get("instance", instance_id)
        print_success(f"Active instance set to: {active}")


@instance.command("connect")
@click.argument("target", required=False)
@click.option("--wait/--no-wait", default=True, show_default=True,
              help="Wait until the instance shows up on the server.")
@click.option("--timeout", "wait_timeout", default=20, show_default=True,
              help="Seconds to wait for the instance to connect.")
def connect_instance(target: Optional[str], wait: bool, wait_timeout: int):
    """Trigger a running Unity Editor to start its MCP session.

    Drops a trigger file (~/.unity-mcp/connect.trigger) that the MCP for Unity
    plugin polls about once per second - same effect as clicking Connect /
    Start Session in the MCP window. TARGET optionally limits the trigger to
    one project (project folder name or project-hash prefix).

    \\b
    Examples:
        unity-mcp instance connect
        unity-mcp instance connect MyProject
        unity-mcp instance connect abc123 --no-wait
    """
    trigger = Path.home() / ".unity-mcp" / "connect.trigger"
    trigger.parent.mkdir(parents=True, exist_ok=True)
    trigger.write_text(target or "", encoding="utf-8")
    print_info(f"Connect trigger written: {trigger}")

    if not wait:
        return

    config = get_config()
    deadline = time.monotonic() + wait_timeout
    while time.monotonic() < deadline:
        try:
            result = run_list_instances(config)
            instances = result.get("instances", []) if isinstance(result, dict) else []
            for inst in instances:
                ident = f"{inst.get('project', '')}@{inst.get('hash', '')}"
                if not target or target.lower() in ident.lower():
                    print_success(f"Unity connected: {ident}")
                    return
        except Exception:
            pass  # server not up yet; keep polling until the deadline
        time.sleep(1)

    print_error(
        "Timed out waiting for Unity to connect. "
        "Is the Editor open, the MCP server running, and the transport set to HTTP?")
    raise SystemExit(1)


@instance.command("current")
def current_instance():
    """Show the currently selected Unity instance.

    \\b
    Examples:
        unity-mcp instance current
    """
    config = get_config()

    # The current instance is typically shown in telemetry or needs to be tracked
    # For now, we can show the configured instance from CLI options
    if config.unity_instance:
        click.echo(f"Configured instance: {config.unity_instance}")
    else:
        print_info(
            "No instance explicitly set. Using default (auto-select single instance).")
        print_info("Use 'unity-mcp instance list' to see available instances.")
        print_info("Use 'unity-mcp instance set <id>' to select one.")

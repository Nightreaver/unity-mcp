"""Tests for `unity-mcp instance connect` (connect trigger file)."""

from unittest.mock import patch

import pytest
from click.testing import CliRunner

from cli.commands.instance import instance


@pytest.fixture
def runner():
    return CliRunner()


@pytest.fixture
def fake_home(tmp_path, monkeypatch):
    monkeypatch.setattr("cli.commands.instance.Path.home", lambda: tmp_path)
    return tmp_path


def test_connect_writes_trigger_file(runner, fake_home):
    result = runner.invoke(instance, ["connect", "--no-wait"])
    assert result.exit_code == 0
    trigger = fake_home / ".unity-mcp" / "connect.trigger"
    assert trigger.exists()
    assert trigger.read_text(encoding="utf-8") == ""


def test_connect_writes_target_into_trigger(runner, fake_home):
    result = runner.invoke(instance, ["connect", "MyProject", "--no-wait"])
    assert result.exit_code == 0
    trigger = fake_home / ".unity-mcp" / "connect.trigger"
    assert trigger.read_text(encoding="utf-8") == "MyProject"


def test_connect_wait_succeeds_when_instance_appears(runner, fake_home):
    response = {"success": True, "instances": [
        {"project": "MyProject", "hash": "abc123"}]}
    with patch("cli.commands.instance.run_list_instances", return_value=response):
        result = runner.invoke(instance, ["connect", "MyProject"])
    assert result.exit_code == 0
    assert "MyProject@abc123" in result.output


def test_connect_wait_times_out(runner, fake_home):
    response = {"success": True, "instances": []}
    with patch("cli.commands.instance.run_list_instances", return_value=response):
        result = runner.invoke(
            instance, ["connect", "--timeout", "1"])
    assert result.exit_code == 1
    assert "Timed out" in result.output

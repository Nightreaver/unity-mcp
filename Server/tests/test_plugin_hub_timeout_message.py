"""A PluginHub command timeout must carry text: a bare asyncio.TimeoutError has an empty
str(), which reached clients as {"success": false, "message": null}."""

import asyncio
from unittest.mock import AsyncMock, MagicMock, patch

import pytest

from transport.plugin_hub import PluginHub


def _run_timed_out(command, params):
    async def exercise():
        websocket = MagicMock()
        websocket.send_json = AsyncMock()  # Unity never answers

        async def wait_for(future, timeout):
            raise asyncio.TimeoutError()

        with patch.object(PluginHub, "_get_connection", AsyncMock(return_value=websocket)), \
             patch.object(PluginHub, "_lock", asyncio.Lock()), \
             patch.object(PluginHub, "_pending", {}), \
             patch("transport.plugin_hub.asyncio.wait_for", side_effect=wait_for):
            try:
                return await PluginHub.send_command("sess-1", command, params)
            finally:
                assert not PluginHub._pending

    return asyncio.run(exercise())


def test_timeout_raises_with_command_and_wait_in_message():
    with pytest.raises(asyncio.TimeoutError) as excinfo:
        _run_timed_out("execute_code", {"action": "execute", "code": "return 1;"})
    text = str(excinfo.value)
    assert "execute_code" in text
    assert "within" in text


def test_execute_code_timeout_over_http_reports_text(monkeypatch):
    """End to end over the HTTP path: timeout -> send_with_unity_instance -> execute_code."""
    from types import SimpleNamespace

    import transport.unity_transport as ut
    from services.tools.execute_code import execute_code

    monkeypatch.setattr(ut, "_is_http_transport", lambda: True)
    monkeypatch.setattr(ut.config, "http_remote_hosted", False)
    monkeypatch.setattr(
        ut.PluginHub, "send_command_for_instance",
        AsyncMock(side_effect=asyncio.TimeoutError(
            "Unity did not respond to 'execute_code' within 35.0s")),
    )
    monkeypatch.setattr(
        "services.tools.execute_code.get_unity_instance_from_context",
        AsyncMock(return_value=None),
    )

    result = asyncio.run(execute_code(SimpleNamespace(), action="execute", code="return 1;"))
    assert result["success"] is False
    assert result["message"], result
    assert "did not respond to 'execute_code'" in result["message"]

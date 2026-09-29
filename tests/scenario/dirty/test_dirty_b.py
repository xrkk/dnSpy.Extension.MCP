"""ACC-043 dirty-state B: scenario leaves its recording client open (check 3)."""

SCENARIO_ID = "S-DIRTY-B"
DECLARED_TOOLS = ["edit_status"]


def test_dirty_b_client_not_closed(scenario_env):
    """Dirty B: query edit status but do NOT close the client (check 3 must BLOCK)."""
    scenario_env.client.edit_status()

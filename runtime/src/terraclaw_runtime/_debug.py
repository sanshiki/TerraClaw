"""Debug output control — global verbose flag."""
import logging

VERBOSE = False

_logger = logging.getLogger("terraclaw.dprint")


def dprint(*args, **kwargs):
    if VERBOSE:
        text = " ".join(str(a) for a in args)
        print(text, flush=True)
        _logger.debug(text)

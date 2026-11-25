from inventory.config import DEFAULT_LOW_STOCK

_levels = {}


def add_stock(item, qty):
    _levels[item] = _levels.get(item, 0) + qty
    return _levels[item]


def remove_stock(item, qty):
    if _levels.get(item, 0) < qty:
        raise ValueError("not enough stock")
    _levels[item] -= qty
    return _levels[item]


def is_low(item):
    return _levels.get(item, 0) <= DEFAULT_LOW_STOCK


def receive_shipment(shipment):
    for item, qty in shipment.items():
        add_stock(item, qty)

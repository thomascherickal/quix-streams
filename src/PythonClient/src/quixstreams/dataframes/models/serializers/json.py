import json
from typing import Callable, Union, List, Mapping, Optional, Any

from .base import Serializer, Deserializer, SerializationContext
from .exceptions import SerializationError

__all__ = ("JSONSerializer", "JSONDeserializer")


class JSONSerializer(Serializer):
    def __init__(
        self,
        dumps: Callable[[Any, ...], Union[str, bytes]] = json.dumps,
        dumps_kwargs: Optional[Mapping] = None,
    ):
        """
        Serializer the returns data in json format.
        :param dumps: a function to serialize objects to json. Default - `json.dumps`
        :param dumps_kwargs: a dict with keyword arguments for `dumps()` function.
        """
        self._dumps = dumps
        self._dumps_kwargs = dumps_kwargs or {}

    def __call__(self, value: Any, ctx: SerializationContext) -> Union[str, bytes]:
        try:
            return self._dumps(value, **self._dumps_kwargs)
        except (ValueError, TypeError) as exc:
            raise SerializationError(str(exc)) from exc


class JSONDeserializer(Deserializer):
    def __init__(
        self,
        column_name: Optional[str] = None,
        loads: Callable[
            [Union[str, bytes, bytearray], ...], Union[List, Mapping]
        ] = json.loads,
        loads_kwargs: Optional[Mapping] = None,
    ):
        """
        Deserializer that expects data to be
        :param column_name:
        :param loads:
        :param loads_kwargs:
        """
        super().__init__(column_name=column_name)
        self._loads = loads
        self._loads_kwargs = loads_kwargs or {}

    def __call__(self, value: bytes, ctx: SerializationContext) -> Union[List, Mapping]:
        try:
            deserialized = self._loads(value, **self._loads_kwargs)
            return self._to_dict(deserialized)
        except (ValueError, TypeError) as exc:
            raise SerializationError(str(exc)) from exc
